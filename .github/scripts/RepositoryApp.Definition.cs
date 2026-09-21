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
    private static string MaterializeDefinition(ChallengeDocument document, Func<BuildImage, string>? resolveImage = null)
    {
        var output = new JsonObject
        {
            ["schemaVersion"] = DefinitionSchemaVersion(document.Mode)
        };
        foreach (var key in new[] { "runtime", "checker", "checkerFixInput", "flagInjection", "patch" })
        {
            if (document.Root.Children.TryGetValue(new YamlScalarNode(key), out var value))
                output[key] = YamlToJson(value);
        }
        ReplaceBuildImages(output, document, resolveImage);
        CanonicalizeDefinition(output, document);
        return output.ToJsonString(JsonOptions);
    }

    private static void CanonicalizeDefinition(JsonObject output, ChallengeDocument document)
    {
        if (output["patch"] is JsonObject patch)
        {
            output["patchEntrypoint"] = patch["entrypoint"]?.DeepClone();
            output["patchCommand"] = patch["command"]?.DeepClone();
            output["patchTimeoutSeconds"] = patch["timeoutSeconds"]?.DeepClone();
            output["readyTimeoutSeconds"] = patch["readyTimeoutSeconds"]?.DeepClone();
            output.Remove("patch");
        }
        if (output["runtime"] is not JsonObject runtime)
            return;

        runtime["allocation"] = EnumNumber(
            runtime["allocation"]?.ToString(),
            ("Shared", 0),
            ("PerTeam", 1));
        if (runtime["flagSource"] is not null)
            runtime["flagSource"] = EnumNumber(
                runtime["flagSource"]?.ToString(),
                ("Static", 0),
                ("PerTeam", 1),
                ("AwdRotation", 2));
        if (runtime["definition"] is not JsonObject definition)
            return;
        definition["kind"] = definition["kind"]?.ToString().ToLowerInvariant();
        definition["environment"] ??= new JsonObject();
        definition["labels"] ??= new JsonObject();
        definition["egressPolicy"] ??= 0;

        var endpoints = runtime["endpoints"] as JsonArray;
        if (endpoints is not null)
        {
            var bindings = new JsonArray();
            var portMappings = new JsonObject();
            foreach (var endpoint in endpoints.OfType<JsonObject>())
            {
                var protocol = endpoint["protocol"]?.ToString() ?? "Http";
                var port = endpoint["containerPort"]!.GetValue<int>();
                var serviceName = endpoint["serviceName"]?.ToString();
                var template = endpoint["urlTemplate"]?.ToString() ?? (protocol.Equals("Http", StringComparison.OrdinalIgnoreCase)
                    ? "http://{HOST}:{PORT}/"
                    : "tcp://{HOST}:{PORT}");
                bindings.Add(new JsonObject
                {
                    ["urlTemplate"] = template,
                    ["exposure"] = EnumNumber(
                        endpoint["exposure"]?.ToString(),
                        ("OwnerOnly", 0),
                        ("Participants", 1)),
                    ["containerPort"] = port,
                    ["serviceName"] = serviceName
                });
                if (string.Equals(definition["kind"]?.ToString(), "container", StringComparison.Ordinal))
                    portMappings[port.ToString()] = 0;
            }
            runtime["urlBindings"] = bindings;
            runtime.Remove("endpoints");
            if (definition["kind"]?.ToString() == "container")
                definition["portMappings"] = portMappings;
        }
        else if (definition["kind"]?.ToString() == "container")
        {
            definition["portMappings"] ??= new JsonObject();
        }

        if (runtime["controlCheck"] is JsonObject control)
        {
            var port = control["containerPort"]!.GetValue<int>();
            runtime["controlCheckUrlBinding"] = new JsonObject
            {
                ["urlTemplate"] = $"http://{{HOST}}:{{PORT}}{control["path"]?.ToString() ?? "/"}",
                ["exposure"] = 0,
                ["containerPort"] = port
            };
            runtime.Remove("controlCheck");
            if (definition["kind"]?.ToString() == "container")
                ((JsonObject)definition["portMappings"]!)[port.ToString()] = 0;
        }

        if (definition["kind"]?.ToString() == "compose")
            MaterializeComposeDefinition(document, runtime, definition);
    }

    private static void MaterializeComposeDefinition(
        ChallengeDocument document,
        JsonObject runtime,
        JsonObject definition)
    {
        var relativeFile = definition["file"]?.ToString()
            ?? throw new InvalidOperationException(
                $"{document.RelativeDirectory}: Compose definition requires file.");
        var composeRoot = Mapping(LoadYaml(SafeChildPath(document.Directory, relativeFile)));
        var services = Mapping(composeRoot.Children[new YamlScalarNode("services")]);
        var serviceImages = definition["serviceImages"] as JsonObject
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
        definition["composeYaml"] = writer.ToString();
        definition.Remove("file");
        definition.Remove("serviceImages");

        var limits = runtime["limits"] as JsonObject
            ?? throw new InvalidOperationException(
                $"{document.RelativeDirectory}: Compose runtime requires limits.");
        var resources = definition["serviceResources"] as JsonObject;
        if (resources is null)
        {
            if (services.Children.Count != 1)
                throw new InvalidOperationException(
                    $"{document.RelativeDirectory}: multi-service Compose requires definition.serviceResources.");
            resources = new JsonObject();
            resources[((YamlScalarNode)services.Children.Keys.Single()).Value!] =
                limits.DeepClone();
        }
        definition["serviceResources"] = resources;
    }

    private static int EnumNumber(string? value, params (string Name, int Value)[] values) =>
        values.FirstOrDefault(item =>
            string.Equals(item.Name, value, StringComparison.OrdinalIgnoreCase)) is var match
            && match.Name is not null
                ? match.Value
                : throw new InvalidOperationException($"Unsupported enum value '{value}'.");

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
        if (node["runtime"]?["definition"] is JsonObject definition)
        {
            if (definition["kind"]?.ToString().Equals("Container", StringComparison.OrdinalIgnoreCase) == true)
                definition["image"] = ResolveReference(definition["image"]);
            else if (definition["kind"]?.ToString().Equals("Compose", StringComparison.OrdinalIgnoreCase) == true
                && definition["serviceImages"] is JsonObject images)
                foreach (var service in images.ToArray())
                    images[service.Key] = ResolveReference(service.Value);
        }
        if (node["checker"] is JsonObject checker)
        {
            var job = document.Mode == "Awd" ? checker["job"]!.AsObject() : checker;
            job["image"] = ResolveReference(job["image"]);
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

    private static int DefinitionSchemaVersion(string mode) => NormalizeMode(mode) switch
    {
        "Ctf" => 3,
        "Awd" or "Awdp" => 4,
        "Koh" => 1,
        _ => throw new InvalidOperationException("Unsupported mode.")
    };

    private static int RulesSchemaVersion(string mode) => NormalizeMode(mode) switch
    {
        "Ctf" => 2,
        "Awd" or "Awdp" => 4,
        "Koh" => 1,
        _ => throw new InvalidOperationException("Unsupported mode.")
    };

    private static string MaterializeRules(YamlMappingNode entry, string mode)
    {
        if (entry.Children.ContainsKey(new YamlScalarNode("baseScore")))
            throw new InvalidOperationException("baseScore is obsolete. Put scores in mode-specific rules (CTF scoreCurve; AWDP break/fix; AWD attackPoints; KoH controlPointsPerInterval).");
        var rules = YamlToJson(entry.Children[new YamlScalarNode("rules")])!.AsObject();
        if (rules["schemaVersion"]?.GetValue<int>() != RulesSchemaVersion(mode))
            throw new InvalidOperationException($"{mode} rules require schemaVersion {RulesSchemaVersion(mode)}.");
        return rules.ToJsonString(JsonOptions);
    }
}
