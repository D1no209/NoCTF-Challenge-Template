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
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };
    private static readonly string[] ForbiddenKeys =
    [
        "provider", "runnerPool", "hostPort", "namespace", "ingress",
        "targetUrl", "targetPort"
    ];
    private static readonly List<string> ApplySummary = [];

    public static async Task<int> RunAsync(
        string[] args,
        [CallerFilePath] string sourceFile = "")
    {
        var root = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(sourceFile)!, "..", ".."));
        if (args.Length == 0)
            return Fail("Usage: repository.cs <scaffold|validate|discover|plan|build|apply|self-test>");
        try
        {
            return args[0] switch
            {
                "scaffold" => await ScaffoldAsync(root, args[1..]),
                "validate" => Validate(root),
                "discover" => Discover(root, args[1..]),
                "plan" => Plan(root, args[1..]),
                "build" => await BuildAsync(root, args[1..]),
                "apply" => await ApplyAsync(root, args[1..]),
                "self-test" => SelfTest(root),
                _ => Fail($"Unknown command '{args[0]}'.")
            };
        }
        catch (Exception exception)
        {
            if (args[0] == "apply")
                WriteApplySummary($"Failed: {exception.Message}");
            Console.Error.WriteLine(exception.Message);
            return 1;
        }
    }

    private static int SelfTest(string root)
    {
        var errors = ValidateRepository(root);
        if (errors.Count != 0)
            throw new InvalidOperationException(string.Join(Environment.NewLine, errors));
        if (NormalizeSegment("Web Security") != "web-security")
            throw new InvalidOperationException("Direction normalization failed.");
        foreach (var sample in new[]
                 {
                     ("Ctf", "None"),
                     ("Ctf", "Container"),
                     ("Ctf", "Compose"),
                     ("Awd", "Container"),
                     ("Awd", "Compose"),
                     ("Awdp", "Container"),
                     ("Koh", "Container")
                 })
        {
            var manifest = Mapping(LoadYamlText(ScaffoldManifest(
                 Guid.NewGuid(),
                 sample.Item1,
                 "Self Test",
                 "Web",
                 sample.Item2)));
            var runtime = manifest.Children.TryGetValue(
                new YamlScalarNode("runtime"),
                out var runtimeNode)
                ? Mapping(runtimeNode)
                : null;
            if ((sample.Item2 == "None") != (runtime is null))
                throw new InvalidOperationException($"Scaffold runtime mismatch for {sample}.");
            if (sample.Item1 == "Awd"
                && NullScalar(runtime!, "flagSource") != "AwdRotation")
                throw new InvalidOperationException("AWD flagSource must follow the runtime.");
            if (sample.Item1 == "Koh"
                && !runtime!.Children.ContainsKey(new YamlScalarNode("controlCheck")))
                throw new InvalidOperationException("KoH controlCheck must follow the runtime.");
        }
        ValidateWorkflow(Path.Combine(root, ".github", "workflows", "challenge-ci.yml"));
        ValidateWorkflow(Path.Combine(root, ".github", "workflows", "scaffold-challenge.yml"));
        foreach (var example in Directory.EnumerateFiles(
                     Path.Combine(root, "examples"),
                     "challenge.example.yml",
                     SearchOption.AllDirectories))
        {
            var document = ReadChallenge(root, example);
            var exampleErrors = new List<string>();
            ValidateChallenge(
                document,
                document.Mode,
                [],
                [],
                [],
                exampleErrors);
            if (exampleErrors.Count != 0)
                throw new InvalidOperationException(string.Join(Environment.NewLine, exampleErrors));
        }
        try
        {
            _ = SafeChildPath(root, "../escape");
            throw new InvalidOperationException("Path traversal test failed.");
        }
        catch (InvalidOperationException exception) when (exception.Message.Contains("escapes", StringComparison.Ordinal))
        {
        }
        Console.WriteLine("Self-test passed.");
        return 0;
    }

    private static void ValidateWorkflow(string path)
    {
        _ = LoadYaml(path);
        var text = File.ReadAllText(path);
        if (text.Contains("pull_request_target", StringComparison.Ordinal))
            throw new InvalidOperationException($"{path} must not use pull_request_target.");
        foreach (Match match in Regex.Matches(text, @"uses:\s+[^\s]+@([^\s]+)"))
            if (!Regex.IsMatch(match.Groups[1].Value, "^[0-9a-f]{40}$"))
                throw new InvalidOperationException(
                    $"{path} contains an Action that is not pinned to a commit SHA.");
        var tokenIndex = text.IndexOf("NOCTF_BOT_TOKEN", StringComparison.Ordinal);
        var applyIndex = Regex.Match(text, @"(?m)^  apply:\s*$").Index;
        var tokenLines = Regex.Matches(text, @"(?m)^.*NOCTF_BOT_TOKEN.*$").Count;
        if (tokenIndex >= 0
            && (tokenLines != 1
                || applyIndex == 0
                || tokenIndex < applyIndex))
            throw new InvalidOperationException(
                "NOCTF_BOT_TOKEN must appear only in the apply job.");
    }

    private static void WriteApplySummary(string outcome)
    {
        var path = Environment.GetEnvironmentVariable("GITHUB_STEP_SUMMARY");
        if (string.IsNullOrWhiteSpace(path))
            return;
        var lines = new[] { "## NoCTF GitOps Apply", "", outcome, "" }
            .Concat(ApplySummary.Select(line => $"- {line}"));
        File.AppendAllLines(path, lines);
    }
}
