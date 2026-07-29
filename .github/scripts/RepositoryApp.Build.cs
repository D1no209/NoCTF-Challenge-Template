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
    private static int Discover(string root, string[] args)
    {
        var changed = DiscoverPaths(root, Required(args, "--base"), Required(args, "--head"));
        var result = changed.Select(relative =>
        {
            var challenge = ReadChallenge(
                root,
                Path.Combine(root, relative, "challenge.yml"));
            return new
            {
                challenge = relative,
                challengeId = challenge.Id,
                attachments = Sequence(challenge.Root, "attachments")
                    .Select(item => new
                    {
                        id = Guid.Parse(Scalar(item, "id")),
                        path = Scalar(item, "path")
                    })
                    .ToArray(),
                images = BuildImages(challenge.Root)
                    .Select(item => new
                    {
                        item.Key,
                        item.Context,
                        item.Dockerfile,
                        sourceHash = SourceHash(challenge.Directory, item)
                    })
                    .ToArray()
            };
        }).ToArray();
        Console.WriteLine(JsonSerializer.Serialize(result, JsonOptions));
        return 0;
    }

    private static List<string> DiscoverPaths(string root, string @base, string head)
    {
        var output = Run(root, "git", ["diff", "--name-only", @base, head]);
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var normalized = NormalizePath(path);
            var segments = normalized.Split('/');
            if (segments.Length >= 3
                && !segments[0].StartsWith('.')
                && File.Exists(Path.Combine(root, segments[0], segments[1], "challenge.yml")))
                result.Add($"{segments[0]}/{segments[1]}");
        }
        return result.Order().ToList();
    }

    private static int Plan(string root, string[] args)
    {
        var selected = args.Contains("--all", StringComparer.Ordinal)
            ? FindChallenges(root).Select(path => Relative(root, Path.GetDirectoryName(path)!)).ToHashSet()
            : DiscoverPaths(root, Required(args, "--base"), Required(args, "--head")).ToHashSet();
        var sha = Option(args, "--sha") ?? Run(root, "git", ["rev-parse", "HEAD"]).Trim();
        var items = new List<object>();
        foreach (var path in FindChallenges(root))
        {
            var challenge = ReadChallenge(root, path);
            if (!selected.Contains(challenge.RelativeDirectory))
                continue;
            foreach (var image in BuildImages(challenge.Root))
            {
                var sourceHash = SourceHash(challenge.Directory, image);
                items.Add(new
                {
                    challenge = challenge.RelativeDirectory,
                    key = image.Key,
                    context = image.Context,
                    dockerfile = image.Dockerfile,
                    target = image.Target,
                    platforms = image.Platforms,
                    sourceHash,
                    gitSha = sha
                });
            }
        }
        var json = JsonSerializer.Serialize(items, JsonOptions);
        Console.WriteLine(json);
        var outputFile = Environment.GetEnvironmentVariable("GITHUB_OUTPUT");
        if (args.Contains("--github-output", StringComparer.Ordinal) && !string.IsNullOrEmpty(outputFile))
            File.AppendAllText(outputFile, $"matrix={JsonSerializer.Serialize(items)}{Environment.NewLine}");
        return 0;
    }

    private static async Task<int> BuildAsync(string root, string[] args)
    {
        var challengePath = NormalizePath(Required(args, "--challenge"));
        var key = Required(args, "--key");
        var challenge = ReadChallenge(root, Path.Combine(root, challengePath, "challenge.yml"));
        var image = BuildImages(challenge.Root).Single(item => item.Key == key);
        var sourceHash = SourceHash(challenge.Directory, image);
        var sha = Option(args, "--sha") ?? Run(root, "git", ["rev-parse", "HEAD"]).Trim();
        var push = args.Contains("--push", StringComparer.Ordinal);
        var tags = RegistryTags(challengePath, key, sourceHash, sha);
        var command = new List<string>
        {
            "buildx", "build",
            "--file", Path.Combine(challenge.Directory, image.Dockerfile),
            "--platform", string.Join(',', image.Platforms)
        };
        if (!string.IsNullOrWhiteSpace(image.Target))
        {
            command.Add("--target");
            command.Add(image.Target!);
        }
        foreach (var tag in tags)
        {
            command.Add("--tag");
            command.Add(tag);
        }
        if (push)
            command.Add("--push");
        else if (image.Platforms.Length == 1)
            command.Add("--load");
        else
        {
            command.Add("--output");
            command.Add("type=cacheonly");
        }
        command.Add(Path.Combine(challenge.Directory, image.Context));
        await RunStreamingAsync(root, "docker", command);
        return 0;
    }

}
