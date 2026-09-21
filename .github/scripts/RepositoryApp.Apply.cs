using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using YamlDotNet.RepresentationModel;

internal static partial class RepositoryApp
{
    private static async Task<int> ApplyAsync(string root, string[] args)
    {
        ApplySummary.Clear();
        var validationErrors = ValidateRepository(root);
        if (validationErrors.Count > 0)
            throw new InvalidOperationException(string.Join(Environment.NewLine, validationErrors));
        var @base = Option(args, "--base");
        var head = Option(args, "--head");
        if ((@base is null) != (head is null))
            throw new InvalidOperationException("Deletion evidence requires both --base and --head.");
        if (@base is not null && head is not null)
        {
            @base = Run(root, "git", ["rev-parse", "--verify", "--end-of-options", $"{@base}^{{commit}}"]).Trim();
            head = Run(root, "git", ["rev-parse", "--verify", "--end-of-options", $"{head}^{{commit}}"]).Trim();
        }
        var apiUrl = Option(args, "--api-url")
            ?? Environment.GetEnvironmentVariable("NOCTF_API_URL")
            ?? throw new InvalidOperationException("NOCTF_API_URL is required.");
        var token = Environment.GetEnvironmentVariable("NOCTF_BOT_TOKEN")
            ?? throw new InvalidOperationException("NOCTF_BOT_TOKEN is required.");
        using var client = new NoCtfClient(apiUrl, token);
        var competition = Mapping(LoadYaml(Path.Combine(root, "competition.yml")));
        if (!bool.TryParse(Scalar(competition, "initialized", "false"), out var initialized) || !initialized)
            throw new InvalidOperationException("Initialize the competition repository before Apply.");
        var competitionId = Guid.Parse(Scalar(competition, "competitionId"));
        var identity = await client.GetAsync("/api/v1/auth/me");
        var competitionState = await client.GetAsync($"/api/v1/admin/competitions/{competitionId}");
        RequireCompetitionManagement(competitionState!);
        var competitionResource = CompetitionResource(competitionState!);
        if (NormalizeApiMode(competitionResource["mode"]!.ToString()) != NormalizeMode(Scalar(competition, "mode")))
            throw new InvalidOperationException("Manifest mode does not match the NoCTF competition.");
        var status = competitionResource["status"]?.ToString();
        if (string.Equals(status, "Finished", StringComparison.OrdinalIgnoreCase) || status == "5")
            throw new InvalidOperationException("Finished competitions cannot be changed by GitOps.");

        var challengeDocuments = FindChallenges(root)
            .Select(path => ReadChallenge(root, path))
            .ToDictionary(item => item.RelativeDirectory);
        var currentInstances = await client.GetItemsAsync($"/api/v1/admin/competitions/{competitionId}/challenges?includeDeleted=true");
        var desiredInstances = Sequence(competition, "challenges").Select(item => Guid.Parse(Scalar(item, "id"))).ToHashSet();
        foreach (var removed in currentInstances.Where(item => item["deletedAt"] is null
            && !desiredInstances.Contains(item["id"]!.GetValue<Guid>())))
            ApplySummary.Add($"CompetitionChallenge `{removed["id"]}` planned for soft deletion (absent from the complete manifest).");
        foreach (var entry in Sequence(competition, "challenges"))
        {
            var id = Guid.Parse(Scalar(entry, "id"));
            var existing = currentInstances.SingleOrDefault(item => item["id"]!.GetValue<Guid>() == id);
            var template = challengeDocuments[NormalizePath(Scalar(entry, "challenge"))];
            if (existing is not null && existing["challengeId"]!.GetValue<Guid>() != template.Id)
                throw new InvalidOperationException($"CompetitionChallenge {id} already belongs to another Challenge.");
        }
        var prepared = new List<(ChallengeDocument Document, string Definition, string Statement)>();
        foreach (var document in challengeDocuments.Values)
        {
            var definition = MaterializeDefinition(document);
            var current = await client.GetAsync($"/api/v1/admin/challenges/{document.Id}?includeDeleted=true", allowNotFound: true);
            if (current is not null)
            {
                RequireChallengeManagement(current, identity!);
                await ValidateAttachmentIdentitiesAsync(client, document);
            }
            var entry = Sequence(competition, "challenges").SingleOrDefault(item =>
                NormalizePath(Scalar(item, "challenge")) == document.RelativeDirectory);
            if (entry is not null) _ = MaterializeRules(entry, document.Mode);
            // Only ordinary read/CRUD APIs are used. Full mode validation remains in each write endpoint.
            prepared.Add((document, definition, await File.ReadAllTextAsync(
                SafeChildPath(document.Directory, Scalar(document.Root, "statement")))));
            ApplySummary.Add($"Challenge `{document.RelativeDirectory}`: local/read-only checks passed; {(current is null ? "create" : current["deletedAt"] is not null ? "restore/reconcile" : "reconcile existing UUID")}.");
        }
        if (args.Contains("--dry-run", StringComparer.Ordinal))
        {
            foreach (var message in ApplySummary) Console.WriteLine(message);
            WriteApplySummary("Local/read-only checks succeeded. No resources were changed; write-time server validation is still required.");
            Console.WriteLine("Local/read-only checks passed. No resources were changed; write-time server validation is still required.");
            return 0;
        }
        foreach (var item in prepared)
            await ApplyChallengeAsync(client, item.Document, item.Definition, item.Statement, identity!);
        await ApplyCompetitionChallengesAsync(client, competitionId, competition, challengeDocuments);
        foreach (var item in prepared)
        {
            var current = await client.GetAsync($"/api/v1/admin/challenges/{item.Document.Id}");
            await UpdateChallengeMetadataAsync(client, item.Document, current!, item.Definition, item.Statement,
                Scalar(item.Document.Root, "visibility"));
        }
        if (@base is not null && head is not null)
            await DeleteRemovedChallengesAsync(
                client,
                root,
                @base,
                head,
                challengeDocuments.Values.Select(item => item.Id).ToHashSet());
        WriteApplySummary("Succeeded.");
        Console.WriteLine("Apply completed.");
        return 0;
    }

    private static async Task ApplyChallengeAsync(NoCtfClient client, ChallengeDocument document,
        string definitionJson, string statement, JsonObject identity)
    {
        var current = await client.GetAsync(
            $"/api/v1/admin/challenges/{document.Id}?includeDeleted=true",
            allowNotFound: true);
        if (current is null)
        {
            current = await client.CreateAsync("/api/v1/admin/challenges", new
            {
                id = document.Id,
                mode = document.Mode,
                visibility = "Private",
                title = Scalar(document.Root, "title"),
                description = statement,
                direction = Scalar(document.Root, "direction"),
                definitionJson
            }, $"/api/v1/admin/challenges/{document.Id}", candidate =>
                candidate["ownerId"]!.GetValue<Guid>() == identity["userId"]!.GetValue<Guid>()
                && ChallengeMetadataMatches(candidate, document, definitionJson, statement, "Private"));
        }
        RequireChallengeManagement(current!, identity);
        if (current!["deletedAt"] is not null)
        {
            await client.SendAsync(HttpMethod.Post, $"/api/v1/admin/challenges/{document.Id}/restore");
            current = await client.GetAsync($"/api/v1/admin/challenges/{document.Id}");
        }
        if (ChallengeMetadataMatches(
                current!,
                document,
                definitionJson,
                statement,
                Scalar(document.Root, "visibility"))
            && await ChallengeChildrenMatchAsync(client, document))
        {
            ApplySummary.Add($"Challenge `{document.RelativeDirectory}` skipped (already converged).");
            return;
        }

        current = await UpdateChallengeMetadataAsync(client, document, current!, definitionJson, statement,
            current!["visibility"]!.GetValue<string>());
        await ApplyAttachmentsAsync(client, document);
        await ApplyFlagsAsync(client, document);
        ApplySummary.Add($"Challenge `{document.RelativeDirectory}` converged.");
    }

    private static async Task<JsonObject> UpdateChallengeMetadataAsync(
        NoCtfClient client,
        ChallengeDocument document,
        JsonObject current,
        string definitionJson,
        string statement,
        string visibility)
    {
        if (ChallengeMetadataMatches(current, document, definitionJson, statement, visibility))
            return current;
        return await client.SendAsync(HttpMethod.Patch, $"/api/v1/admin/challenges/{document.Id}", new
        {
            content = new
            {
                mode = document.Mode,
                visibility,
                title = Scalar(document.Root, "title"),
                description = statement,
                direction = Scalar(document.Root, "direction"),
                definitionJson
            }
        });
    }

    private static async Task DeleteRemovedChallengesAsync(
        NoCtfClient client,
        string root,
        string @base,
        string head,
        HashSet<Guid> currentIds)
    {
        var deleted = Run(
                root,
                "git",
                ["diff", "--diff-filter=D", "--name-only", @base, head, "--", ":(glob)*/*/challenge.yml"])
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(NormalizePath)
            .Where(path => path.Split('/').Length == 3
                           && path.EndsWith("/challenge.yml", StringComparison.Ordinal))
            .ToArray();
        foreach (var path in deleted)
        {
            var yaml = Run(root, "git", ["show", $"{@base}:{path}"]);
            var id = Guid.Parse(Scalar(Mapping(LoadYamlText(yaml)), "id"));
            if (currentIds.Contains(id))
                continue;
            var current = await client.GetAsync(
                $"/api/v1/admin/challenges/{id}?includeDeleted=true",
                allowNotFound: true);
            if (current is null || current["deletedAt"] is not null)
                continue;
            if (current["activeCompetitionReferenceCount"]!.GetValue<int>() != 0)
            {
                Console.WriteLine(
                    $"Skipped Challenge {id}: it is still referenced outside this repository state.");
                continue;
            }
            await client.SendAsync(HttpMethod.Delete, $"/api/v1/admin/challenges/{id}");
            ApplySummary.Add($"Challenge `{id}` soft-deleted from explicit base/head diff.");
        }
    }

    private static bool ChallengeMetadataMatches(
        JsonObject current,
        ChallengeDocument document,
        string definitionJson,
        string statement,
        string visibility) =>
        TextEquals(current, "mode", document.Mode)
        && TextEquals(current, "visibility", visibility)
        && TextEquals(current, "title", Scalar(document.Root, "title"))
        && TextEquals(current, "description", statement)
        && TextEquals(current, "direction", Scalar(document.Root, "direction"))
        && JsonEquivalent(current["definitionJson"]?.ToString(), definitionJson);

    private static async Task<bool> ChallengeChildrenMatchAsync(
        NoCtfClient client,
        ChallengeDocument document)
    {
        var attachments = await GetManagedAttachmentsAsync(client, document.Id);
        var desiredAttachments = Sequence(document.Root, "attachments")
            .ToDictionary(item => Guid.Parse(Scalar(item, "id")));
        if (attachments.Any(item =>
                item["deletedAt"] is null
                && !desiredAttachments.ContainsKey(item["id"]!.GetValue<Guid>())))
            return false;
        foreach (var pair in desiredAttachments)
        {
            var current = attachments.SingleOrDefault(item => item["id"]!.GetValue<Guid>() == pair.Key);
            if (current is null || current["deletedAt"] is not null)
                return false;
            var bytes = await File.ReadAllBytesAsync(
                SafeChildPath(document.Directory, Scalar(pair.Value, "path")));
            if (!string.Equals(
                    current["sha256"]?.ToString(),
                    Convert.ToHexString(SHA256.HashData(bytes)),
                    StringComparison.OrdinalIgnoreCase))
                return false;
        }

        var flags = await client.GetItemsAsync(
            $"/api/v1/admin/challenges/{document.Id}/flags?includeDeleted=true");
        var desiredFlags = Sequence(document.Root, "flags")
            .ToDictionary(item => Guid.Parse(Scalar(item, "id")));
        return flags.Where(item => item["deletedAt"] is null).All(item =>
                   desiredFlags.ContainsKey(item["id"]!.GetValue<Guid>()))
               && desiredFlags.All(pair =>
               {
                   var current = flags.SingleOrDefault(item => item["id"]!.GetValue<Guid>() == pair.Key);
                   return current is not null
                          && current["deletedAt"] is null
                          && TextEquals(current, "flag", Scalar(pair.Value, "value"))
                          && TextEquals(current, "matchKind", Scalar(pair.Value, "matchKind", "Exact"))
                          && current["specificationKind"] is null
                          && current["specificationId"] is null;
               });
    }

    private static void RequireCompetitionManagement(JsonObject competition)
    {
        if (competition["capabilities"]?["canModerate"]?.GetValue<bool>() != true)
            throw new InvalidOperationException("GitOps requires competition Owner or Manager permission; read-only Judge/Observer access is insufficient.");
        if (CompetitionResource(competition)["deletedAt"] is not null)
            throw new InvalidOperationException("A deleted competition cannot be managed by GitOps.");
    }

    private static JsonObject CompetitionResource(JsonObject response) =>
        response["competition"]?.AsObject()
        ?? throw new InvalidOperationException("NoCTF returned an invalid competition response.");

    private static void RequireChallengeManagement(JsonObject challenge, JsonObject identity)
    {
        var actorId = identity["userId"]!.GetValue<Guid>();
        if (identity["role"]?.ToString() == "Administrator"
            || challenge["ownerId"]!.GetValue<Guid>() == actorId
            || challenge["managerIds"]!.AsArray().Any(id => id!.GetValue<Guid>() == actorId))
            return;
        throw new InvalidOperationException($"Challenge {challenge["id"]}: the Bot needs independent template Owner/Manager permission; competition Manager is not enough.");
    }
}
