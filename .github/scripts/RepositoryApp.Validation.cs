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
        if (Scalar(competition, "apiVersion", "") != "gitops.noctf.dev/v2")
            errors.Add("competition.yml apiVersion must be gitops.noctf.dev/v2.");
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
                if (item.Children.ContainsKey(new YamlScalarNode("baseScore")))
                    errors.Add($"{challengePath}: baseScore was removed; use mode-specific scoring rules.");
                if (!item.Children.TryGetValue(new YamlScalarNode("rules"), out var rulesNode)
                    || rulesNode is not YamlMappingNode rules)
                    errors.Add($"competition.yml requires typed rules for '{challengePath}'.");
                else
                    ValidateRules(rules, mode, challengePath, errors);
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
                        if (string.IsNullOrWhiteSpace(Scalar(hint, "content", "")))
                            errors.Add($"{challengePath}: hint {hintText} requires non-empty content.");
                        var publishedAt = NullScalar(hint, "publishedAt");
                        if (publishedAt is not null)
                        {
                            try { _ = JsonSerializer.Deserialize<DateTimeOffset>(JsonSerializer.Serialize(publishedAt)); }
                            catch (JsonException) { errors.Add($"{challengePath}: hint {hintText} publishedAt must be an ISO-8601 timestamp or null."); }
                        }
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
        if (!Regex.IsMatch(prefix, @"^[a-z0-9]+(?:-[a-z0-9]+)*/[a-z0-9]+(?:-[a-z0-9]+)*$"))
            errors.Add($"{prefix}: challenge paths must use safe lowercase direction/slug segments.");
        RequireOnlyKeys(
            document.Root,
            prefix,
            errors,
            "apiVersion", "kind", "id", "mode", "title", "direction", "visibility",
            "statement", "attachments", "flags", "build", "definition");
        if (Scalar(document.Root, "apiVersion") != "gitops.noctf.dev/v2")
            errors.Add($"{prefix}: apiVersion must be gitops.noctf.dev/v2.");
        if (Scalar(document.Root, "kind") != "ChallengeTemplate")
            errors.Add($"{prefix}: kind must be ChallengeTemplate.");
        if (string.IsNullOrWhiteSpace(Scalar(document.Root, "title")) || Scalar(document.Root, "title").Length > 160)
            errors.Add($"{prefix}: title is required and must not exceed 160 characters.");
        if (Scalar(document.Root, "direction").Length > 96)
            errors.Add($"{prefix}: direction must not exceed 96 characters.");
        if (Scalar(document.Root, "visibility") is not ("Private" or "Shared"))
            errors.Add($"{prefix}: visibility must be Private or Shared.");
        if (document.Id == Guid.Empty || !challengeIds.Add(document.Id))
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
            if (!NormalizePath(Scalar(attachment, "path")).StartsWith("attachments/", StringComparison.Ordinal))
                errors.Add($"{prefix}: participant attachments must be inside attachments/, never solution/ or runtime/.");
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

        if (!document.Root.Children.TryGetValue(new YamlScalarNode("definition"), out var definitionNode)
            || definitionNode is not YamlMappingNode definition)
        {
            errors.Add($"{prefix}: definition is required.");
            return;
        }
        ValidateDefinition(document, definition, prefix, errors);
        var hasRuntime = definition.Children.ContainsKey(new YamlScalarNode("runtime"));
        var hasChecker = definition.Children.ContainsKey(new YamlScalarNode("checker"));
        if (hasRuntime && Sequence(document.Root, "flags").Count > 0)
            errors.Add($"{prefix}: dynamic Runtime Flags are generated by NoCTF; template static flags must be empty.");
        if (definition.Children.ContainsKey(new YamlScalarNode("checkerFixInput"))
            && (document.Mode != "Awdp" || !hasChecker
                || !bool.TryParse(Scalar(definition, "checkerFixInput"), out _)))
            errors.Add($"{prefix}: checkerFixInput must be a boolean on an AWDP challenge with a Checker.");
        if (hasRuntime)
            ValidateRuntime(document, definition, prefix, errors);
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
            if (!Regex.IsMatch(image.Key, @"^[a-z0-9]+(?:-[a-z0-9]+)*$") || image.Key.Length > 64)
                errors.Add($"{prefix}: build image keys must be safe lowercase identifiers, at most 64 characters.");
            var context = SafeChildPath(document.Directory, image.Context);
            var dockerfile = SafeChildPath(document.Directory, image.Dockerfile);
            if (!Directory.Exists(context))
                errors.Add($"{prefix}: build context '{image.Context}' is missing.");
            if (!File.Exists(dockerfile))
                errors.Add($"{prefix}: Dockerfile '{image.Dockerfile}' is missing.");
        }
        ValidateImageReferences(document.Root, BuildImages(document.Root).Select(item => item.Key).ToHashSet(), prefix, errors);
    }

    private static void ValidateDefinition(
        ChallengeDocument document,
        YamlMappingNode definition,
        string prefix,
        List<string> errors)
    {
        RequireOnlyKeys(definition, $"{prefix}.definition", errors,
            "mode", "flagTemplate", "runtime", "checker", "patchEntrypoint", "patchCommand",
            "patchTimeoutSeconds", "readyTimeoutSeconds", "maximumPatchUploadBytes",
            "checkerFixInput", "checkerAllowRoot", "ctf", "awd", "awdp", "koh");
        if (Scalar(definition, "mode", "") != document.Mode)
            errors.Add($"{prefix}: definition.mode must match mode {document.Mode}.");
        var branch = document.Mode.ToLowerInvariant();
        var branches = new[] { "ctf", "awd", "awdp", "koh" }
            .Where(key => definition.Children.ContainsKey(new YamlScalarNode(key))).ToArray();
        if (branches.Length != 1 || branches[0] != branch)
        {
            errors.Add($"{prefix}: definition must contain exactly the {branch} branch.");
            return;
        }
        var modeDefinition = Mapping(definition.Children[new YamlScalarNode(branch)]);
        switch (document.Mode)
        {
            case "Ctf":
                RequireOnlyKeys(modeDefinition, $"{prefix}.definition.ctf", errors, "interactionKind");
                if (Scalar(modeDefinition, "interactionKind", "") is not ("FlagSubmission" or "PatchVerification"))
                    errors.Add($"{prefix}: CTF interactionKind must be FlagSubmission or PatchVerification.");
                break;
            case "Awd":
                RequireOnlyKeys(modeDefinition, $"{prefix}.definition.awd", errors, "flagInjection");
                break;
            case "Awdp":
                RequireOnlyKeys(modeDefinition, $"{prefix}.definition.awdp", errors);
                break;
            case "Koh":
                RequireOnlyKeys(modeDefinition, $"{prefix}.definition.koh", errors);
                break;
        }
    }

    private static void ValidateRules(
        YamlMappingNode rules,
        string mode,
        string challengePath,
        List<string> errors)
    {
        RequireOnlyKeys(rules, $"{challengePath}.rules", errors, "mode", "ctf", "awd", "awdp", "koh");
        var normalized = NormalizeMode(mode);
        if (Scalar(rules, "mode", "") != normalized)
            errors.Add($"{challengePath}: rules.mode must match {normalized}.");
        var branch = normalized.ToLowerInvariant();
        var branches = new[] { "ctf", "awd", "awdp", "koh" }
            .Where(key => rules.Children.ContainsKey(new YamlScalarNode(key))).ToArray();
        if (branches.Length != 1 || branches[0] != branch)
        {
            errors.Add($"{challengePath}: rules must contain exactly the {branch} branch.");
            return;
        }
        RequireOnlyKeys(Mapping(rules.Children[new YamlScalarNode(branch)]),
            $"{challengePath}.rules.{branch}", errors, RuleKeys(normalized));
    }

    private static string[] RuleKeys(string mode) => NormalizeMode(mode) switch
    {
        "Ctf" => ["scoreCurve", "bloodRewards", "maxFlagAttempts", "maxPatchAttempts", "wrongSubmissionPenalty", "flagTemplate"],
        "Awd" => ["attackRewardMode", "attackPoints", "victimDefensePoolPoints", "checkerIntervalSeconds", "serviceHealthyPoints", "serviceUnhealthyPenalty", "flagTemplate"],
        "Awdp" => ["breakScoreCurve", "fixScoreCurve", "requireBreakBeforeFix", "maxBreakSubmissions", "maxFixSubmissions", "flagWrongPenalty", "exploitSucceededPenalty", "serviceAbnormalPenalty", "evaluationDispatchMode", "flagTemplate"],
        "Koh" => ["pollIntervalSeconds", "controlPointsPerInterval"],
        _ => []
    };
}
