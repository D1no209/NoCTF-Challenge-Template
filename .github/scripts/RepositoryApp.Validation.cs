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
    private static int Validate(string root)
    {
        var errors = ValidateRepository(root);
        foreach (var error in errors)
            Console.Error.WriteLine($"error: {error}");
        if (errors.Count != 0)
            return 1;
        Console.WriteLine($"Validated {FindChallenges(root).Count} challenge manifest(s).");
        return 0;
    }

    private static List<string> ValidateRepository(string root)
    {
        var errors = new List<string>();
        var competitionPath = Path.Combine(root, "competition.yml");
        if (!File.Exists(competitionPath))
            return ["competition.yml is missing."];
        var competition = Mapping(LoadYaml(competitionPath));
        var competitionId = Scalar(competition, "competitionId");
        var mode = Scalar(competition, "mode");
        if (!Guid.TryParse(competitionId, out _))
            errors.Add("competition.yml competitionId must be a UUID.");
        if (!IsMode(mode))
            errors.Add("competition.yml mode must be Ctf, Awd, Awdp, or Koh.");

        var manifests = FindChallenges(root);
        var byPath = new Dictionary<string, ChallengeDocument>(StringComparer.OrdinalIgnoreCase);
        var challengeIds = new HashSet<Guid>();
        var attachmentIds = new HashSet<Guid>();
        var flagIds = new HashSet<Guid>();
        foreach (var path in manifests)
        {
            try
            {
                var document = ReadChallenge(root, path);
                byPath.Add(document.RelativeDirectory, document);
                ValidateChallenge(document, mode, challengeIds, attachmentIds, flagIds, errors);
            }
            catch (Exception exception)
            {
                errors.Add($"{Relative(root, path)}: {exception.Message}");
            }
        }

        var instanceIds = new HashSet<Guid>();
        var referencedChallenges = new HashSet<Guid>();
        var hintIds = new HashSet<Guid>();
        if (competition.Children.TryGetValue(new YamlScalarNode("challenges"), out var challengesNode)
            && challengesNode is YamlSequenceNode challenges)
        {
            var orders = new HashSet<int>();
            foreach (var item in challenges.Children.Cast<YamlMappingNode>())
            {
                var idText = Scalar(item, "id");
                var challengePath = NormalizePath(Scalar(item, "challenge"));
                if (!Guid.TryParse(idText, out var id) || !instanceIds.Add(id))
                    errors.Add($"competition.yml has an invalid or duplicate CompetitionChallenge id '{idText}'.");
                if (!int.TryParse(Scalar(item, "order"), out var order) || order < 0 || !orders.Add(order))
                    errors.Add($"competition.yml has an invalid or duplicate order for '{challengePath}'.");
                if (!long.TryParse(Scalar(item, "baseScore"), out var score) || score < 0)
                    errors.Add($"competition.yml has an invalid baseScore for '{challengePath}'.");
                if (!item.Children.TryGetValue(new YamlScalarNode("rules"), out var rulesNode)
                    || rulesNode is not YamlMappingNode rules
                    || Scalar(rules, "schemaVersion") !=
                    (mode.Equals("Awd", StringComparison.OrdinalIgnoreCase) ? "4" : "1"))
                    errors.Add($"competition.yml has an invalid rules schemaVersion for '{challengePath}'.");
                if (!byPath.TryGetValue(challengePath, out var challenge))
                {
                    errors.Add($"competition.yml references missing challenge '{challengePath}'.");
                    continue;
                }
                if (!referencedChallenges.Add(challenge.Id))
                    errors.Add($"Challenge '{challengePath}' is referenced more than once.");
                if (item.Children.TryGetValue(new YamlScalarNode("hints"), out var hintsNode)
                    && hintsNode is YamlSequenceNode hints)
                {
                    foreach (var hint in hints.Children.Cast<YamlMappingNode>())
                    {
                        var hintText = Scalar(hint, "id");
                        if (!Guid.TryParse(hintText, out var hintId) || !hintIds.Add(hintId))
                            errors.Add($"{challengePath} has an invalid or duplicate hint id '{hintText}'.");
                        if (!long.TryParse(Scalar(hint, "cost"), out var cost) || cost < 0)
                            errors.Add($"{challengePath} has an invalid hint cost for '{hintText}'.");
                    }
                }
            }
        }
        else
        {
            errors.Add("competition.yml challenges must be a sequence.");
        }
        return errors;
    }

    private static void ValidateChallenge(
        ChallengeDocument document,
        string competitionMode,
        HashSet<Guid> challengeIds,
        HashSet<Guid> attachmentIds,
        HashSet<Guid> flagIds,
        List<string> errors)
    {
        var prefix = document.RelativeDirectory;
        RequireOnlyKeys(
            document.Root,
            prefix,
            errors,
            "apiVersion", "kind", "id", "mode", "title", "direction", "visibility",
            "statement", "attachments", "flags", "build", "runtime", "checker",
            "flagTemplate", "flagInjection", "patch");
        if (Scalar(document.Root, "apiVersion") != "gitops.noctf.dev/v1")
            errors.Add($"{prefix}: apiVersion must be gitops.noctf.dev/v1.");
        if (Scalar(document.Root, "kind") != "ChallengeTemplate")
            errors.Add($"{prefix}: kind must be ChallengeTemplate.");
        if (!challengeIds.Add(document.Id))
            errors.Add($"{prefix}: duplicate Challenge id {document.Id}.");
        if (!string.Equals(document.Mode, competitionMode, StringComparison.OrdinalIgnoreCase))
            errors.Add($"{prefix}: mode {document.Mode} does not match competition mode {competitionMode}.");
        var direction = document.RelativeDirectory.Split('/')[0];
        if (!string.Equals(
                NormalizeSegment(Scalar(document.Root, "direction")),
                direction,
                StringComparison.Ordinal))
            errors.Add($"{prefix}: direction does not match the directory.");
        var statement = SafeChildPath(document.Directory, Scalar(document.Root, "statement"));
        if (!File.Exists(statement))
            errors.Add($"{prefix}: statement file is missing.");
        Walk(document.Root, (key, _) =>
        {
            if (ForbiddenKeys.Contains(key, StringComparer.OrdinalIgnoreCase))
                errors.Add($"{prefix}: forbidden deployment field '{key}'.");
        });

        foreach (var attachment in Sequence(document.Root, "attachments"))
        {
            var idText = Scalar(attachment, "id");
            if (!Guid.TryParse(idText, out var id) || !attachmentIds.Add(id))
                errors.Add($"{prefix}: invalid or duplicate attachment id '{idText}'.");
            var file = SafeChildPath(document.Directory, Scalar(attachment, "path"));
            if (!File.Exists(file))
                errors.Add($"{prefix}: attachment '{Relative(document.Directory, file)}' is missing.");
        }
        foreach (var flag in Sequence(document.Root, "flags"))
        {
            var idText = Scalar(flag, "id");
            if (!Guid.TryParse(idText, out var id) || !flagIds.Add(id))
                errors.Add($"{prefix}: invalid or duplicate flag id '{idText}'.");
            if (string.IsNullOrEmpty(Scalar(flag, "value")))
                errors.Add($"{prefix}: static flag cannot be empty.");
        }

        var hasRuntime = document.Root.Children.ContainsKey(new YamlScalarNode("runtime"));
        var hasChecker = document.Root.Children.ContainsKey(new YamlScalarNode("checker"));
        if (hasRuntime)
            ValidateRuntime(document, prefix, errors);
        switch (document.Mode.ToLowerInvariant())
        {
            case "ctf" when hasChecker:
                errors.Add($"{prefix}: CTF challenges cannot define a checker.");
                break;
            case "awd" when !hasRuntime || !hasChecker:
                errors.Add($"{prefix}: AWD requires runtime and checker.");
                break;
            case "awdp" when !hasRuntime || !hasChecker:
                errors.Add($"{prefix}: AWDP requires runtime and checker.");
                break;
            case "koh" when !hasRuntime || hasChecker:
                errors.Add($"{prefix}: KoH requires runtime and cannot define a checker job.");
                break;
        }

        foreach (var image in BuildImages(document.Root))
        {
            var context = SafeChildPath(document.Directory, image.Context);
            var dockerfile = SafeChildPath(document.Directory, image.Dockerfile);
            if (!Directory.Exists(context))
                errors.Add($"{prefix}: build context '{image.Context}' is missing.");
            if (!File.Exists(dockerfile))
                errors.Add($"{prefix}: Dockerfile '{image.Dockerfile}' is missing.");
        }
        ValidateImageReferences(document.Root, BuildImages(document.Root).Select(item => item.Key).ToHashSet(), prefix, errors);
    }
}
