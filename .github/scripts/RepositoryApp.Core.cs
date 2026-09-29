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
        "provider", "runnerPool", "namespace", "ingress",
        "targetUrl", "targetPort"
    ];
    private static readonly List<string> ApplySummary = [];

    public static async Task<int> RunAsync(
        string[] args,
        [CallerFilePath] string sourceFile = "")
    {
        var root = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(sourceFile)!, "..", ".."));
        if (args.Length == 0)
            return Fail("Usage: repository.cs <initialize|scaffold|validate|discover|plan|build|apply|readme|self-test>");
        try
        {
            return args[0] switch
            {
                "initialize" => await InitializeAsync(root, args[1..]),
                "scaffold" => await ScaffoldAsync(root, args[1..]),
                "validate" => Validate(root),
                "discover" => Discover(root, args[1..]),
                "plan" => Plan(root, args[1..]),
                "build" => await BuildAsync(root, args[1..]),
                "apply" => await ApplyAsync(root, args[1..]),
                "readme" => GenerateReadme(root),
                "self-test" => await SelfTestAsync(root),
                "contract-fixtures" => await ExportContractFixturesAsync(args[1..]),
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

    private static async Task<int> SelfTestAsync(string root)
    {
        var errors = ValidateRepository(root);
        if (errors.Count != 0)
            throw new InvalidOperationException(string.Join(Environment.NewLine, errors));
        if (NormalizeSegment("Web Security") != "web-security")
            throw new InvalidOperationException("Direction normalization failed.");
        if (NormalizeRuntime("Static (no runtime)") != "None")
            throw new InvalidOperationException("Static runtime normalization failed.");
        if (NormalizeApiMode("0") != "Ctf" || NormalizeApiMode("Koh") != "Koh")
            throw new InvalidOperationException("API mode normalization failed.");
        var initializationFields = ParseIssueForm(
            "### Competition ID\n\n00000000-0000-0000-0000-000000000001\n\n"
            + "### Game Mode\n\nCtf\n");
        if (Field(initializationFields, "Game Mode") != "Ctf")
            throw new InvalidOperationException("Initialization Issue parsing failed.");
        try
        {
            using var invalidClient = new NoCtfClient("file:///tmp/noctf", "token");
            throw new InvalidOperationException("Invalid NoCTF API URL test failed.");
        }
        catch (InvalidOperationException exception) when (
            exception.Message.Contains("HTTP or HTTPS", StringComparison.Ordinal))
        {
        }
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
            var scaffold = ScaffoldManifest(
                Guid.NewGuid(), sample.Item1, "Self Test", "Web", sample.Item2);
            YamlMappingNode manifest;
            try { manifest = Mapping(LoadYamlText(scaffold)); }
            catch (Exception exception)
            {
                throw new InvalidOperationException(
                    $"Generated scaffold is invalid for {sample}:{Environment.NewLine}{scaffold}",
                    exception);
            }
            var definition = Mapping(manifest.Children[new YamlScalarNode("definition")]);
            var runtime = definition.Children.TryGetValue(
                new YamlScalarNode("runtime"), out var runtimeNode)
                ? Mapping(runtimeNode) : null;
            if ((sample.Item2 == "None") != (runtime is null))
                throw new InvalidOperationException($"Scaffold runtime mismatch for {sample}.");
            if (sample.Item1 == "Awd"
                && NullScalar(runtime!, "flagSource") != "AwdRotation")
                throw new InvalidOperationException("AWD flagSource must follow the runtime.");
            if (sample.Item1 == "Koh"
                && (!(runtime!.Children[new YamlScalarNode("urlBindings")] is YamlSequenceNode bindings)
                    || !bindings.Children.Cast<YamlMappingNode>().Any(binding =>
                        bool.TryParse(Scalar(binding, "isControlCheck", "false"), out var control)
                        && control)))
                throw new InvalidOperationException("KoH control-check URL binding must follow the runtime.");
        }
        foreach (var issueForm in Directory.EnumerateFiles(
                     Path.Combine(root, ".github", "ISSUE_TEMPLATE"),
                     "*.yml"))
            if (!Path.GetFileName(issueForm).Equals("config.yml", StringComparison.Ordinal))
                ValidateIssueForm(issueForm);
        foreach (var workflow in Directory.EnumerateFiles(
                     Path.Combine(root, ".github", "workflows"),
                     "*.yml"))
            ValidateWorkflow(workflow);
        var readme = RenderReadme(root);
        if (!readme.Contains("## Challenges", StringComparison.Ordinal))
            throw new InvalidOperationException("README rendering failed.");
        var examples = Path.Combine(root, "examples");
        foreach (var example in Directory.Exists(examples)
                     ? Directory.EnumerateFiles(examples, "challenge.example.yml", SearchOption.AllDirectories)
                     : [])
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
        await ContractTestsAsync();
        Console.WriteLine("Self-test passed.");
        return 0;
    }

    private static void ValidateIssueForm(string path)
    {
        var root = Mapping(LoadYaml(path));
        foreach (var item in Sequence(root, "body"))
        {
            if (!string.Equals(Scalar(item, "type"), "dropdown", StringComparison.Ordinal))
                continue;
            var attributes = Mapping(item.Children[new YamlScalarNode("attributes")]);
            var options = ((YamlSequenceNode)attributes.Children[new YamlScalarNode("options")]).Children
                .Select(node => ((YamlScalarNode)node).Value ?? "");
            if (options.Any(option => option.Equals("None", StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException(
                    $"{path} contains GitHub's reserved dropdown option 'None'.");
        }
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
        var workflow = Path.GetFileName(path);
        if (workflow.Equals("challenge-ci.yml", StringComparison.Ordinal))
        {
            if (text.Contains("NOCTF_BOT_TOKEN", StringComparison.Ordinal)
                || !text.Contains("cancel-in-progress: true", StringComparison.Ordinal))
                throw new InvalidOperationException(
                    "Challenge PR CI must cancel superseded validation and never receive the Bot token.");
        }
        if (workflow.Equals("challenge-deploy.yml", StringComparison.Ordinal))
        {
            var tokenIndex = text.IndexOf("NOCTF_BOT_TOKEN", StringComparison.Ordinal);
            var applyIndex = Regex.Match(text, @"(?m)^  apply:\s*$").Index;
            var tokenLines = Regex.Matches(text, @"(?m)^.*NOCTF_BOT_TOKEN.*$").Count;
            if (tokenLines != 1 || applyIndex == 0 || tokenIndex < applyIndex)
                throw new InvalidOperationException(
                    "NOCTF_BOT_TOKEN must appear only in the apply job.");
            if (!text.Contains("id: apply_config", StringComparison.Ordinal)
                || !text.Contains(
                    "if: steps.apply_config.outputs.configured == 'true'",
                    StringComparison.Ordinal))
                throw new InvalidOperationException(
                    "Challenge CI must skip registry login and Apply until NoCTF configuration is present.");
            if (!text.Contains("queue: max", StringComparison.Ordinal)
                || text.Contains("cancel-in-progress", StringComparison.Ordinal))
                throw new InvalidOperationException(
                    "Challenge main deployment must queue every run without cancellation.");
        }
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
