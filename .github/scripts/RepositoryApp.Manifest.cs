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
    private static ChallengeDocument ReadChallenge(string root, string path)
    {
        var mapping = Mapping(LoadYaml(path));
        var directory = Path.GetDirectoryName(path)!;
        return new(
            Guid.Parse(Scalar(mapping, "id")),
            NormalizeMode(Scalar(mapping, "mode")),
            Relative(root, directory),
            directory,
            mapping);
    }

    private static List<string> FindChallenges(string root) =>
        Directory.EnumerateFiles(root, "challenge.yml", SearchOption.AllDirectories)
            .Where(path => !Relative(root, path).StartsWith(".git/", StringComparison.Ordinal))
            .Order()
            .ToList();

    private static List<BuildImage> BuildImages(YamlMappingNode root)
    {
        if (!root.Children.TryGetValue(new YamlScalarNode("build"), out var buildNode)
            || buildNode is not YamlMappingNode build
            || !build.Children.TryGetValue(new YamlScalarNode("images"), out var imagesNode)
            || imagesNode is not YamlSequenceNode images)
            return [];
        return images.Children.Cast<YamlMappingNode>().Select(image => new BuildImage(
            Scalar(image, "key"),
            Scalar(image, "context"),
            Scalar(image, "dockerfile"),
            NullScalar(image, "target"),
            image.Children.TryGetValue(new YamlScalarNode("platforms"), out var platformsNode)
                ? ((YamlSequenceNode)platformsNode).Children.Select(item => ((YamlScalarNode)item).Value!).ToArray()
                : ["linux/amd64"])).ToList();
    }

    private static string SourceHash(string challengeDirectory, BuildImage image)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        AddHash(hash, JsonSerializer.Serialize(image));
        var context = SafeChildPath(challengeDirectory, image.Context);
        foreach (var file in Directory.EnumerateFiles(context, "*", SearchOption.AllDirectories).Order())
        {
            AddHash(hash, NormalizePath(Path.GetRelativePath(context, file)));
            hash.AppendData(File.ReadAllBytes(file));
        }
        var dockerfile = SafeChildPath(challengeDirectory, image.Dockerfile);
        if (!dockerfile.StartsWith(context + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            hash.AppendData(File.ReadAllBytes(dockerfile));
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private static void AddHash(IncrementalHash hash, string value) =>
        hash.AppendData(Encoding.UTF8.GetBytes(value));

    private static void ValidateImageReferences(
        YamlNode node,
        HashSet<string> buildKeys,
        string prefix,
        List<string> errors)
    {
        if (node is YamlMappingNode mapping)
        {
            if (mapping.Children.TryGetValue(new YamlScalarNode("image"), out var imageNode)
                && imageNode is YamlMappingNode image)
            {
                if (image.Children.TryGetValue(new YamlScalarNode("build"), out var build)
                    && !buildKeys.Contains(((YamlScalarNode)build).Value!))
                    errors.Add($"{prefix}: image references unknown build key '{((YamlScalarNode)build).Value}'.");
                if (image.Children.TryGetValue(new YamlScalarNode("external"), out var external)
                    && !Regex.IsMatch(
                        ((YamlScalarNode)external).Value!,
                        @"^[^@\s]+@sha256:[0-9a-fA-F]{64}$"))
                    errors.Add($"{prefix}: external images must use a digest.");
            }
            foreach (var child in mapping.Children.Values)
                ValidateImageReferences(child, buildKeys, prefix, errors);
        }
        else if (node is YamlSequenceNode sequence)
        {
            foreach (var child in sequence.Children)
                ValidateImageReferences(child, buildKeys, prefix, errors);
        }
    }

    private static YamlNode LoadYaml(string path)
    {
        using var reader = File.OpenText(path);
        var stream = new YamlStream();
        stream.Load(reader);
        return stream.Documents.Single().RootNode;
    }

    private static YamlNode LoadYamlText(string yaml)
    {
        using var reader = new StringReader(yaml);
        var stream = new YamlStream();
        stream.Load(reader);
        return stream.Documents.Single().RootNode;
    }

    private static YamlMappingNode Mapping(YamlNode node) =>
        node as YamlMappingNode ?? throw new InvalidOperationException("YAML root must be a mapping.");

    private static IReadOnlyList<YamlMappingNode> Sequence(YamlMappingNode node, string key) =>
        node.Children.TryGetValue(new YamlScalarNode(key), out var value)
            ? ((YamlSequenceNode)value).Children.Cast<YamlMappingNode>().ToArray()
            : [];

    private static string Scalar(YamlMappingNode node, string key, string? fallback = null)
    {
        if (!node.Children.TryGetValue(new YamlScalarNode(key), out var value))
            return fallback ?? throw new InvalidOperationException($"Required field '{key}' is missing.");
        return ((YamlScalarNode)value).Value ?? fallback ?? "";
    }

    private static string? NullScalar(YamlMappingNode node, string key)
    {
        if (!node.Children.TryGetValue(new YamlScalarNode(key), out var value))
            return null;
        var text = ((YamlScalarNode)value).Value;
        return string.Equals(text, "null", StringComparison.OrdinalIgnoreCase) ? null : text;
    }

    private static JsonNode? YamlToJson(YamlNode node) => node switch
    {
        YamlMappingNode mapping => new JsonObject(mapping.Children.ToDictionary(
            pair => ((YamlScalarNode)pair.Key).Value!,
            pair => YamlToJson(pair.Value))),
        YamlSequenceNode sequence => new JsonArray(sequence.Children.Select(YamlToJson).ToArray()),
        YamlScalarNode scalar => ScalarToJson(scalar),
        _ => null
    };

    private static JsonNode? ScalarToJson(YamlScalarNode scalar)
    {
        var value = scalar.Value;
        if (value is null or "null" or "~")
            return null;
        if (bool.TryParse(value, out var boolean))
            return JsonValue.Create(boolean);
        if (long.TryParse(value, out var integer))
            return JsonValue.Create(integer);
        if (decimal.TryParse(value, out var number))
            return JsonValue.Create(number);
        return JsonValue.Create(value);
    }

    private static void Walk(YamlNode node, Action<string, YamlNode> visit)
    {
        if (node is YamlMappingNode mapping)
        {
            foreach (var pair in mapping.Children)
            {
                var key = ((YamlScalarNode)pair.Key).Value!;
                visit(key, pair.Value);
                Walk(pair.Value, visit);
            }
        }
        else if (node is YamlSequenceNode sequence)
        {
            foreach (var child in sequence.Children)
                Walk(child, visit);
        }
    }
}
