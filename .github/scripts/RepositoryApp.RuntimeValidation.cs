using System.Text.RegularExpressions;
using YamlDotNet.RepresentationModel;

internal static partial class RepositoryApp
{
    private static void ValidateRuntime(
        ChallengeDocument document,
        YamlMappingNode definition,
        string prefix,
        List<string> errors)
    {
        var runtime = Mapping(definition.Children[new YamlScalarNode("runtime")]);
        RequireOnlyKeys(runtime, $"{prefix}.definition.runtime", errors,
            "kind", "allocation", "limits", "ttlSeconds", "operationTimeoutSeconds",
            "flagSource", "egressPolicy", "urlBindings", "container", "compose", "ova");
        if (!runtime.Children.TryGetValue(new YamlScalarNode("limits"), out var limitsNode))
            errors.Add($"{prefix}: definition.runtime.limits is required.");
        else
            RequireOnlyKeys(Mapping(limitsNode), $"{prefix}.definition.runtime.limits", errors,
                "memoryBytes", "nanoCpus", "pidsLimit");

        var allocation = Scalar(runtime, "allocation");
        var expectedAllocation = document.Mode == "Koh" ? "Shared" : "PerTeam";
        if (!string.Equals(allocation, expectedAllocation, StringComparison.Ordinal))
            errors.Add($"{prefix}: {document.Mode} runtime allocation must be {expectedAllocation}.");

        var kind = Scalar(runtime, "kind");
        var payloads = new[] { "container", "compose", "ova" }
            .Count(key => runtime.Children.ContainsKey(new YamlScalarNode(key)));
        if (payloads != 1 || !runtime.Children.ContainsKey(new YamlScalarNode(kind.ToLowerInvariant())))
            errors.Add($"{prefix}: runtime kind must have exactly one matching container, compose, or ova payload.");
        if (kind == "Container" && runtime.Children.TryGetValue(new YamlScalarNode("container"), out var containerNode))
            ValidateContainer(Mapping(containerNode), prefix, errors);
        else if (kind == "Compose" && runtime.Children.TryGetValue(new YamlScalarNode("compose"), out var composeNode))
            ValidateCompose(Mapping(composeNode), prefix, errors);
        else if (kind is not ("Container" or "Compose"))
            errors.Add($"{prefix}: GitOps v2 supports only Container or Compose runtimes.");

        var flagSource = NullScalar(runtime, "flagSource");
        var expectedFlagSource = document.Mode switch
        {
            "Ctf" or "Awdp" => "PerTeam",
            "Awd" => "AwdRotation",
            _ => null
        };
        if (!string.Equals(flagSource, expectedFlagSource, StringComparison.Ordinal))
            errors.Add(expectedFlagSource is null
                ? $"{prefix}: {document.Mode} cannot define runtime.flagSource."
                : $"{prefix}: {document.Mode} runtime.flagSource must be {expectedFlagSource}.");

        if (!runtime.Children.TryGetValue(new YamlScalarNode("urlBindings"), out var bindingsNode)
            || bindingsNode is not YamlSequenceNode bindings)
        {
            errors.Add($"{prefix}: definition.runtime.urlBindings is required.");
        }
        else
        {
            var controlChecks = 0;
            foreach (var item in bindings.Children.Cast<YamlMappingNode>())
            {
                RequireOnlyKeys(item, $"{prefix}.definition.runtime.urlBindings", errors,
                    "urlTemplate", "exposure", "containerPort", "serviceName", "vmId",
                    "guestPort", "isControlCheck");
                if (string.IsNullOrWhiteSpace(Scalar(item, "urlTemplate", "")))
                    errors.Add($"{prefix}: every Runtime URL binding requires urlTemplate.");
                if (bool.TryParse(Scalar(item, "isControlCheck", "false"), out var control) && control)
                    controlChecks++;
            }
            if (document.Mode == "Koh" && controlChecks != 1)
                errors.Add($"{prefix}: KoH requires exactly one control-check URL binding.");
            if (document.Mode != "Koh" && controlChecks != 0)
                errors.Add($"{prefix}: only KoH may define a control-check URL binding.");
        }

        if (definition.Children.TryGetValue(new YamlScalarNode("checker"), out var checkerNode))
        {
            var checker = Mapping(checkerNode);
            RequireOnlyKeys(checker, $"{prefix}.definition.checker", errors,
                "image", "command", "environment", "timeoutSeconds", "targetServiceName");
            var hasTargetService = NullScalar(checker, "targetServiceName") is not null;
            if ((kind == "Compose") != hasTargetService)
                errors.Add($"{prefix}: checker.targetServiceName is required only for Compose runtimes.");
        }
    }

    private static void ValidateContainer(
        YamlMappingNode container,
        string prefix,
        List<string> errors)
    {
        RequireOnlyKeys(container, $"{prefix}.definition.runtime.container", errors,
            "image", "command", "environment", "labels", "portMappings", "security",
            "flagEnvironmentVariableName", "internalPorts");
        if (!container.Children.ContainsKey(new YamlScalarNode("image")))
            errors.Add($"{prefix}: Container runtime requires image.");
        if (!container.Children.TryGetValue(new YamlScalarNode("portMappings"), out var mappingsNode)
            || mappingsNode is not YamlSequenceNode mappings)
            errors.Add($"{prefix}: Container runtime requires portMappings.");
        else
            foreach (var mapping in mappings.Children.Cast<YamlMappingNode>())
            {
                RequireOnlyKeys(mapping, $"{prefix}.definition.runtime.container.portMappings", errors,
                    "containerPort", "hostPort");
                if (Scalar(mapping, "hostPort", "") != "0")
                    errors.Add($"{prefix}: Docker hostPort must be 0 for platform allocation.");
            }
    }

    private static void ValidateCompose(
        YamlMappingNode compose,
        string prefix,
        List<string> errors)
    {
        RequireOnlyKeys(compose, $"{prefix}.definition.runtime.compose", errors,
            "file", "serviceImages", "environment", "labels", "flagEnvironmentVariables",
            "serviceResources");
        if (!compose.Children.ContainsKey(new YamlScalarNode("file")))
            errors.Add($"{prefix}: Compose runtime requires file.");
        if (!compose.Children.TryGetValue(new YamlScalarNode("serviceImages"), out var imagesNode)
            || imagesNode is not YamlMappingNode { Children.Count: > 0 })
            errors.Add($"{prefix}: Compose runtime requires non-empty serviceImages.");
        if (!compose.Children.TryGetValue(new YamlScalarNode("serviceResources"), out var resourcesNode)
            || resourcesNode is not YamlSequenceNode { Children.Count: > 0 })
            errors.Add($"{prefix}: Compose runtime requires serviceResources.");
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
