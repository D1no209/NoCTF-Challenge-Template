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
    private static JsonObject MaterializeDefinition(
        ChallengeDocument document,
        Func<BuildImage, string>? resolveImage = null)
    {
        var output = YamlToJson(
            document.Root.Children[new YamlScalarNode("definition")])!.AsObject();
        ReplaceBuildImages(output, document, resolveImage);
        MaterializeComposeDefinition(output, document);
        CanonicalizeDefinition(output);
        return output;
    }

    private static void CanonicalizeDefinition(JsonObject definition)
    {
        definition["flagTemplate"] ??= null;
        definition["runtime"] ??= null;
        definition["checker"] ??= null;
        definition["patchEntrypoint"] ??= null;
        definition["patchCommand"] ??= new JsonArray();
        definition["patchTimeoutSeconds"] ??= null;
        definition["readyTimeoutSeconds"] ??= null;
        definition["maximumPatchUploadBytes"] ??= null;
        definition["checkerFixInput"] ??= false;
        definition["checkerAllowRoot"] ??= false;
        foreach (var branch in new[] { "ctf", "awd", "awdp", "koh" })
            definition[branch] ??= null;
        if (definition["awd"] is JsonObject awd)
            awd["flagInjection"] ??= null;
        if (definition["checker"] is JsonObject checker)
        {
            checker["command"] ??= new JsonArray();
            checker["environment"] ??= new JsonObject();
            checker["targetServiceName"] ??= null;
        }
        if (definition["runtime"] is not JsonObject runtime)
            return;
        runtime["ttlSeconds"] ??= null;
        runtime["operationTimeoutSeconds"] ??= null;
        runtime["flagSource"] ??= "Static";
        runtime["egressPolicy"] ??= "Isolated";
        foreach (var branch in new[] { "container", "compose", "ova" })
            runtime[branch] ??= null;
        if (runtime["urlBindings"] is JsonArray bindings)
            foreach (var binding in bindings.OfType<JsonObject>())
            {
                binding["containerPort"] ??= null;
                binding["serviceName"] ??= null;
                binding["vmId"] ??= null;
                binding["guestPort"] ??= null;
                binding["isControlCheck"] ??= false;
            }
    }

    private static void MaterializeComposeDefinition(
        JsonObject output,
        ChallengeDocument document)
    {
        if (output["runtime"]?["compose"] is not JsonObject compose)
            return;
        var relativeFile = compose["file"]?.ToString()
            ?? throw new InvalidOperationException(
                $"{document.RelativeDirectory}: Compose definition requires file.");
        var composeRoot = Mapping(LoadYaml(SafeChildPath(document.Directory, relativeFile)));
        var services = Mapping(composeRoot.Children[new YamlScalarNode("services")]);
        var serviceImages = compose["serviceImages"] as JsonObject
            ?? throw new InvalidOperationException(
                $"{document.RelativeDirectory}: Compose definition requires serviceImages.");
        foreach (var servicePair in services.Children)
        {
            var serviceName = ((YamlScalarNode)servicePair.Key).Value!;
            if (serviceImages[serviceName] is not JsonValue image)
                throw new InvalidOperationException(
                    $"{document.RelativeDirectory}: serviceImages is missing '{serviceName}'.");
            Mapping(servicePair.Value).Children[new YamlScalarNode("image")] =
                new YamlScalarNode(image.GetValue<string>());
        }
        using var writer = new StringWriter();
        new YamlStream(new YamlDocument(composeRoot)).Save(writer, assignAnchors: false);
        compose["composeYaml"] = writer.ToString();
        compose.Remove("file");
        compose.Remove("serviceImages");
    }

    private static void ReplaceBuildImages(JsonNode node, ChallengeDocument document, Func<BuildImage, string>? resolveImage = null)
    {
        string ResolveReference(JsonNode? node)
        {
            if (node is not JsonObject reference || reference.Count != 1)
                throw new InvalidOperationException("Image references must contain exactly one build or external entry.");
            if (reference["external"] is JsonValue external && external.TryGetValue<string>(out var digest)
                && IsImageDigest(digest))
                return digest;
            if (reference["build"] is JsonValue value && value.TryGetValue<string>(out var key))
            {
                var image = BuildImages(document.Root).SingleOrDefault(item => item.Key == key)
                    ?? throw new InvalidOperationException("Image references an undeclared build key.");
                var resolved = resolveImage is null ? ResolveImage(document, image) : resolveImage(image);
                if (!IsImageDigest(resolved))
                    throw new InvalidOperationException("The resolved build image does not contain a sha256 digest.");
                return resolved;
            }
            throw new InvalidOperationException("External images must be pinned to a sha256 digest.");
        }
        if (node["runtime"] is JsonObject runtime)
        {
            if (runtime["container"] is JsonObject container)
                container["image"] = ResolveReference(container["image"]);
            else if (runtime["compose"]?["serviceImages"] is JsonObject images)
                foreach (var service in images.ToArray())
                    images[service.Key] = ResolveReference(service.Value);
        }
        if (node["checker"] is JsonObject checker)
        {
            checker["image"] = ResolveReference(checker["image"]);
        }
    }

    private static string ResolveImage(ChallengeDocument document, BuildImage image)
    {
        var sourceHash = SourceHash(document.Directory, image);
        var registry = Environment.GetEnvironmentVariable("NOCTF_IMAGE_REGISTRY") ?? "ghcr";
        var tag = RegistryTags(document.RelativeDirectory, image.Key, sourceHash, "resolve")
            .First(value => registry == "custom"
                ? !value.StartsWith("ghcr.io/", StringComparison.Ordinal)
                : value.StartsWith("ghcr.io/", StringComparison.Ordinal));
        tag = tag[..tag.LastIndexOf(':')] + $":src-{sourceHash}";
        var output = Run(document.Directory, "docker", ["buildx", "imagetools", "inspect", tag, "--format", "{{json .Manifest.Digest}}"]);
        var digest = JsonSerializer.Deserialize<string>(output.Trim()) ?? output.Trim().Trim('"');
        ApplySummary.Add($"Image `{document.RelativeDirectory}/{image.Key}` resolved to `{digest}`.");
        return $"{tag[..tag.LastIndexOf(':')]}@{digest}";
    }

    private static List<string> RegistryTags(
        string challenge,
        string key,
        string sourceHash,
        string sha)
    {
        var repository = (Environment.GetEnvironmentVariable("GITHUB_REPOSITORY") ?? "local/noctf-competition")
            .ToLowerInvariant();
        var suffix = $"{challenge.Replace('/', '-')}-{key}".ToLowerInvariant();
        var tags = new List<string>
        {
            $"ghcr.io/{repository}/{suffix}:src-{sourceHash}",
            $"ghcr.io/{repository}/{suffix}:git-{sha}"
        };
        var host = Environment.GetEnvironmentVariable("CUSTOM_REGISTRY_HOST");
        var ns = Environment.GetEnvironmentVariable("CUSTOM_REGISTRY_NAMESPACE");
        if (!string.IsNullOrWhiteSpace(host) || !string.IsNullOrWhiteSpace(ns))
        {
            if (string.IsNullOrWhiteSpace(host) || string.IsNullOrWhiteSpace(ns))
                throw new InvalidOperationException("Custom Registry host and namespace must be configured together.");
            tags.Add($"{host}/{ns}/{suffix}:src-{sourceHash}");
            tags.Add($"{host}/{ns}/{suffix}:git-{sha}");
        }
        return tags;
    }

    private static JsonObject MaterializeRules(YamlMappingNode entry, string mode)
    {
        if (entry.Children.ContainsKey(new YamlScalarNode("baseScore")))
            throw new InvalidOperationException("baseScore is obsolete. Put scores in the typed mode branch (CTF scoreCurve; AWDP breakScoreCurve/fixScoreCurve; AWD attackPoints; KoH controlPointsPerInterval).");
        var rules = YamlToJson(entry.Children[new YamlScalarNode("rules")])!.AsObject();
        if (!string.Equals(rules["mode"]?.ToString(), NormalizeMode(mode), StringComparison.Ordinal))
            throw new InvalidOperationException($"{mode} rules must declare the matching mode.");
        CanonicalizeRules(rules);
        return rules;
    }

    private static void CanonicalizeRules(JsonObject rules)
    {
        foreach (var branch in new[] { "ctf", "awd", "awdp", "koh" })
            rules[branch] ??= null;
        if (rules["ctf"] is JsonObject ctf)
        {
            AddNulls(ctf, "scoreCurve", "bloodRewards", "maxFlagAttempts", "maxPatchAttempts",
                "wrongSubmissionPenalty", "flagTemplate");
            if (ctf["bloodRewards"] is JsonArray { Count: 0 }) ctf["bloodRewards"] = null;
            CanonicalizeCurve(ctf["scoreCurve"]);
        }
        if (rules["awd"] is JsonObject awd)
            AddNulls(awd, "attackRewardMode", "attackPoints", "victimDefensePoolPoints",
                "checkerIntervalSeconds", "serviceHealthyPoints", "serviceUnhealthyPenalty", "flagTemplate");
        if (rules["awdp"] is JsonObject awdp)
        {
            AddNulls(awdp, "breakScoreCurve", "fixScoreCurve", "maxBreakSubmissions",
                "maxFixSubmissions", "requireBreakBeforeFix", "flagWrongPenalty",
                "exploitSucceededPenalty", "serviceAbnormalPenalty", "evaluationDispatchMode", "flagTemplate");
            CanonicalizeCurve(awdp["breakScoreCurve"]);
            CanonicalizeCurve(awdp["fixScoreCurve"]);
        }
        if (rules["koh"] is JsonObject koh)
            AddNulls(koh, "pollIntervalSeconds", "controlPointsPerInterval");
    }

    private static void AddNulls(JsonObject value, params string[] properties)
    {
        foreach (var property in properties)
            value[property] ??= null;
    }

    private static void CanonicalizeCurve(JsonNode? value)
    {
        if (value is JsonObject curve)
            curve["customExpression"] ??= null;
    }
}
