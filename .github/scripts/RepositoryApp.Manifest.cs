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
using System.Buffers.Binary;

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

    private static List<string> FindChallenges(string root)
    {
        var manifests = new List<string>();
        foreach (var direction in Directory.EnumerateDirectories(root)
            .Where(path => !Path.GetFileName(path).StartsWith('.')))
        {
            _ = SafeChildPath(root, Relative(root, direction));
            foreach (var directory in Directory.EnumerateDirectories(direction))
            {
                _ = SafeChildPath(root, Relative(root, directory));
                var manifest = Path.Combine(directory, "challenge.yml");
                if (File.Exists(manifest))
                    manifests.Add(SafeChildPath(directory, "challenge.yml"));
            }
        }
        return manifests.Order(StringComparer.Ordinal).ToList();
    }

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
        AddHash(hash, "noctf-source-v2");
        AddHash(hash, JsonSerializer.Serialize(image));
        var context = SafeChildPath(challengeDirectory, image.Context);
        var dockerfile = SafeChildPath(challengeDirectory, image.Dockerfile);
        var gitRoot = new DirectoryInfo(challengeDirectory);
        while (gitRoot is not null && !Directory.Exists(Path.Combine(gitRoot.FullName, ".git"))
            && !File.Exists(Path.Combine(gitRoot.FullName, ".git")))
            gitRoot = gitRoot.Parent;
        var modes = new Dictionary<string, string>(StringComparer.Ordinal);
        if (gitRoot is not null)
        {
            foreach (var entry in Run(gitRoot.FullName, "git", ["ls-files", "--stage", "-z", "--",
                Relative(gitRoot.FullName, context), Relative(gitRoot.FullName, dockerfile)])
                .Split('\0', StringSplitOptions.RemoveEmptyEntries))
            {
                var separator = entry.IndexOf('\t');
                var metadata = entry[..separator].Split(' ');
                if (metadata[2] != "0")
                    throw new InvalidOperationException("Resolve Git merge conflicts before calculating image source identity.");
                modes[entry[(separator + 1)..]] = metadata[0];
            }
        }
        void AddFile(string kind, string relative, string file)
        {
            AddHash(hash, kind);
            AddHash(hash, relative);
            AddHash(hash, gitRoot is not null && modes.TryGetValue(Relative(gitRoot.FullName, file), out var mode)
                ? mode : "untracked");
            using var stream = File.OpenRead(file);
            Span<byte> length = stackalloc byte[8];
            BinaryPrimitives.WriteInt64BigEndian(length, stream.Length);
            hash.AppendData(length);
            hash.AppendData(SHA256.HashData(stream));
        }
        foreach (var file in Directory.EnumerateFiles(context, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
        {
            _ = SafeChildPath(challengeDirectory, Path.GetRelativePath(challengeDirectory, file));
            AddFile("context-file", NormalizePath(Path.GetRelativePath(context, file)), file);
        }
        if (!dockerfile.StartsWith(context + Path.DirectorySeparatorChar,
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            AddFile("external-dockerfile", NormalizePath(image.Dockerfile), dockerfile);
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private static void AddHash(IncrementalHash hash, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        Span<byte> length = stackalloc byte[8];
        BinaryPrimitives.WriteInt64BigEndian(length, bytes.Length);
        hash.AppendData(length);
        hash.AppendData(bytes);
    }

    private static void ValidateImageReferences(
        YamlMappingNode root,
        HashSet<string> buildKeys,
        string prefix,
        List<string> errors)
    {
        void ValidateReference(YamlNode? value, string path)
        {
            if (value is not YamlMappingNode reference || reference.Children.Count != 1)
            {
                errors.Add($"{prefix}.{path}: use exactly one build or external image reference.");
                return;
            }
            var pair = reference.Children.Single();
            var text = (pair.Value as YamlScalarNode)?.Value;
            if (pair.Key is YamlScalarNode { Value: "build" } && text is not null && buildKeys.Contains(text))
                return;
            if (pair.Key is YamlScalarNode { Value: "external" } && IsImageDigest(text))
                return;
            errors.Add($"{prefix}.{path}: build must name a declared image; external must be pinned to a sha256 digest.");
        }
        if (root.Children.TryGetValue(new YamlScalarNode("definition"), out var definitionNode)
            && Mapping(definitionNode).Children.TryGetValue(new YamlScalarNode("runtime"), out var runtimeNode))
        {
            var runtime = Mapping(runtimeNode);
            if (runtime.Children.TryGetValue(new YamlScalarNode("container"), out var containerNode))
            {
                Mapping(containerNode).Children.TryGetValue(new YamlScalarNode("image"), out var image);
                ValidateReference(image, "definition.runtime.container.image");
            }
            else if (runtime.Children.TryGetValue(new YamlScalarNode("compose"), out var composeNode))
            {
                if (!Mapping(composeNode).Children.TryGetValue(new YamlScalarNode("serviceImages"), out var services)
                    || services is not YamlMappingNode serviceImages || serviceImages.Children.Count == 0)
                    errors.Add($"{prefix}: Compose serviceImages must be a non-empty mapping.");
                else
                    foreach (var service in serviceImages.Children)
                        ValidateReference(service.Value, $"definition.runtime.compose.serviceImages.{((YamlScalarNode)service.Key).Value}");
            }
            if (Mapping(definitionNode).Children.TryGetValue(new YamlScalarNode("checker"), out var checkerNode))
            {
                Mapping(checkerNode).Children.TryGetValue(new YamlScalarNode("image"), out var image);
                ValidateReference(image, "definition.checker.image");
            }
        }
    }

    private static bool IsImageDigest(string? value) => value is not null
        && Regex.IsMatch(value, @"^[^@\s]+@sha256:[0-9a-fA-F]{64}$");

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
        if (scalar.Style is YamlDotNet.Core.ScalarStyle.SingleQuoted or YamlDotNet.Core.ScalarStyle.DoubleQuoted)
            return JsonValue.Create(value);
        if (value is null or "null" or "~")
            return null;
        if (bool.TryParse(value, out var boolean))
            return JsonValue.Create(boolean);
        if (Regex.IsMatch(value, @"^-?(0|[1-9][0-9]*)(\.[0-9]+)?([eE][+-]?[0-9]+)?$"))
            return JsonNode.Parse(value);
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
