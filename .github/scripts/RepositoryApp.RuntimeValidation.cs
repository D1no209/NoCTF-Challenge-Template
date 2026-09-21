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
    private static void ValidateRuntime(
        ChallengeDocument document,
        string prefix,
        List<string> errors)
    {
        var runtime = Mapping(document.Root.Children[new YamlScalarNode("runtime")]);
        RequireOnlyKeys(
            runtime,
            $"{prefix}.runtime",
            errors,
            "allocation", "flagSource", "definition", "limits", "endpoints", "controlCheck", "ttlSeconds", "operationTimeoutSeconds");
        if (!runtime.Children.TryGetValue(new YamlScalarNode("limits"), out var limitsNode))
            errors.Add($"{prefix}: runtime.limits is required.");
        else
            RequireOnlyKeys(
                Mapping(limitsNode),
                $"{prefix}.runtime.limits",
                errors,
                "memoryBytes", "nanoCpus", "pidsLimit");

        var allocation = Scalar(runtime, "allocation");
        var expectedAllocation = document.Mode.Equals("Koh", StringComparison.OrdinalIgnoreCase)
            ? "Shared"
            : "PerTeam";
        if (!string.Equals(allocation, expectedAllocation, StringComparison.OrdinalIgnoreCase))
            errors.Add($"{prefix}: {document.Mode} runtime allocation must be {expectedAllocation}.");

        var definition = Mapping(runtime.Children[new YamlScalarNode("definition")]);
        var kind = Scalar(definition, "kind");
        switch (kind.ToLowerInvariant())
        {
            case "container":
                RequireOnlyKeys(
                    definition,
                    $"{prefix}.runtime.definition",
                    errors,
                    "kind", "image", "command", "entrypoint", "environment", "labels",
                    "internalPorts", "flagEnvironmentVariableName", "security", "egressPolicy");
                break;
            case "compose":
                RequireOnlyKeys(
                    definition,
                    $"{prefix}.runtime.definition",
                    errors,
                    "kind", "file", "serviceImages", "environment", "labels",
                    "flagEnvironmentVariables", "serviceResources");
                break;
            default:
                errors.Add($"{prefix}: runtime.definition.kind must be Container or Compose.");
                break;
        }

        var mode = document.Mode.ToLowerInvariant();
        var flagSource = NullScalar(runtime, "flagSource");
        if (mode == "awd" && !string.Equals(flagSource, "AwdRotation", StringComparison.OrdinalIgnoreCase))
            errors.Add($"{prefix}: AWD runtime.flagSource must be AwdRotation.");
        if (mode == "ctf" && !string.Equals(flagSource, "PerTeam", StringComparison.OrdinalIgnoreCase))
            errors.Add($"{prefix}: CTF runtime.flagSource must be PerTeam.");
        if (mode == "awdp" && !string.Equals(flagSource, "PerTeam", StringComparison.OrdinalIgnoreCase))
            errors.Add($"{prefix}: AWDP runtime.flagSource must be PerTeam.");
        if (mode == "koh" && flagSource is not null)
            errors.Add($"{prefix}: {document.Mode} cannot define runtime.flagSource.");

        if (mode == "awdp")
        {
            var ports = definition.Children.TryGetValue(
                new YamlScalarNode("internalPorts"),
                out var portNode)
                ? portNode as YamlSequenceNode
                : null;
            if (!kind.Equals("Container", StringComparison.OrdinalIgnoreCase)
                || ports?.Children.Count != 1)
                errors.Add($"{prefix}: AWDP requires a Container with exactly one internalPorts value.");
        }
        if (mode == "koh"
            && !runtime.Children.ContainsKey(new YamlScalarNode("controlCheck")))
            errors.Add($"{prefix}: KoH runtime.controlCheck is required.");

        if (document.Root.Children.TryGetValue(new YamlScalarNode("checker"), out var checkerNode))
        {
            var checker = Mapping(checkerNode);
            var hasTargetService = checker.Children.ContainsKey(new YamlScalarNode("targetServiceName"));
            if (kind.Equals("Compose", StringComparison.OrdinalIgnoreCase) != hasTargetService)
                errors.Add(
                    $"{prefix}: checker.targetServiceName is required only for Compose runtimes.");
        }
    }

    private static void RequireOnlyKeys(
        YamlMappingNode node,
        string path,
        List<string> errors,
        params string[] allowed)
    {
        var allowedSet = allowed.ToHashSet(StringComparer.Ordinal);
        foreach (var key in node.Children.Keys.Cast<YamlScalarNode>())
            if (key.Value is not null && !allowedSet.Contains(key.Value))
                errors.Add($"{path}: unknown field '{key.Value}'.");
    }
}
