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
        var apiUrl = Option(args, "--api-url")
            ?? Environment.GetEnvironmentVariable("NOCTF_API_URL")
            ?? throw new InvalidOperationException("NOCTF_API_URL is required.");
        var token = Environment.GetEnvironmentVariable("NOCTF_BOT_TOKEN")
            ?? throw new InvalidOperationException("NOCTF_BOT_TOKEN is required.");
        using var client = new NoCtfClient(apiUrl, token);
        var competition = Mapping(LoadYaml(Path.Combine(root, "competition.yml")));
        var competitionId = Guid.Parse(Scalar(competition, "competitionId"));
        var competitionState = await client.GetAsync($"/api/v1/admin/competitions/{competitionId}");
        var status = competitionState?["status"]?.ToString();
        if (string.Equals(status, "Finished", StringComparison.OrdinalIgnoreCase) || status == "5")
            throw new InvalidOperationException("Finished competitions cannot be changed by GitOps.");

        var challengeDocuments = FindChallenges(root)
            .Select(path => ReadChallenge(root, path))
            .ToDictionary(item => item.RelativeDirectory);
        foreach (var document in challengeDocuments.Values)
            await ApplyChallengeAsync(client, document);
        await ApplyCompetitionChallengesAsync(client, competitionId, competition, challengeDocuments);
        var @base = Option(args, "--base");
        var head = Option(args, "--head");
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

    private static async Task ApplyChallengeAsync(NoCtfClient client, ChallengeDocument document)
    {
        var current = await client.GetAsync(
            $"/api/v1/admin/challenges/{document.Id}?includeDeleted=true",
            allowNotFound: true);
        var definitionJson = MaterializeDefinition(document);
        var statement = await File.ReadAllTextAsync(
            SafeChildPath(document.Directory, Scalar(document.Root, "statement")));
        if (current is null)
        {
            current = await client.SendAsync(HttpMethod.Post, "/api/v1/admin/challenges", new
            {
                id = document.Id,
                mode = document.Mode,
                visibility = "Private",
                title = Scalar(document.Root, "title"),
                description = statement,
                direction = Scalar(document.Root, "direction"),
                definitionJson
            });
        }
        else if (current["deletedAt"] is not null)
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

        current = await UpdateChallengeMetadataAsync(client, document, current!, definitionJson, statement, "Private");
        await ApplyAttachmentsAsync(client, document);
        await ApplyFlagsAsync(client, document);
        current = await client.GetAsync($"/api/v1/admin/challenges/{document.Id}");
        await UpdateChallengeMetadataAsync(
            client,
            document,
            current!,
            definitionJson,
            statement,
            Scalar(document.Root, "visibility"));
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
        return await client.SendAsync(HttpMethod.Put, $"/api/v1/admin/challenges/{document.Id}", new
        {
            mode = document.Mode,
            visibility,
            title = Scalar(document.Root, "title"),
            description = statement,
            direction = Scalar(document.Root, "direction"),
            definitionJson,
            expectedRevision = current["revision"]!.GetValue<int>()
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
        TextEquals(current, "visibility", visibility)
        && TextEquals(current, "title", Scalar(document.Root, "title"))
        && TextEquals(current, "description", statement)
        && TextEquals(current, "direction", Scalar(document.Root, "direction"))
        && JsonEquivalent(current["definitionJson"]?.ToString(), definitionJson);

    private static async Task<bool> ChallengeChildrenMatchAsync(
        NoCtfClient client,
        ChallengeDocument document)
    {
        var attachments = await client.GetItemsAsync(
            $"/api/v1/admin/challenges/{document.Id}/attachments?includeDeleted=true");
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
                          && current["specificationKind"] is null
                          && current["specificationId"] is null;
               });
    }
}
