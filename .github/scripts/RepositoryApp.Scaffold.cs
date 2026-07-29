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
    private static async Task<int> ScaffoldAsync(string root, string[] args)
    {
        var eventPath = Required(args, "--event");
        using var eventDocument = JsonDocument.Parse(await File.ReadAllTextAsync(eventPath));
        var issue = eventDocument.RootElement.GetProperty("issue");
        var association = issue.GetProperty("author_association").GetString();
        if (association is not ("OWNER" or "MEMBER" or "COLLABORATOR"))
            return Fail("Issue author is not allowed to scaffold challenges.");
        var fields = ParseIssueForm(issue.GetProperty("body").GetString() ?? "");
        var title = Field(fields, "Title");
        var direction = NormalizeSegment(Field(fields, "Direction"));
        var slug = NormalizeSegment(Field(fields, "Slug"));
        var mode = NormalizeMode(Field(fields, "Game Mode"));
        var runtime = NormalizeRuntime(Field(fields, "Runtime Type"));
        var owner = Field(fields, "Owner").Trim().TrimStart('@');
        var score = long.Parse(Field(fields, "Base Score"));
        var order = int.Parse(Field(fields, "Order"));
        if (score < 0 || order < 0)
            return Fail("Base Score and Order must be non-negative.");
        if (!Regex.IsMatch(slug, "^[a-z0-9]+(?:-[a-z0-9]+)*$"))
            return Fail("Slug is invalid.");
        if (!Regex.IsMatch(owner, "^[A-Za-z0-9](?:[A-Za-z0-9-]{0,37}[A-Za-z0-9])?$"))
            return Fail("Owner must be a valid GitHub login.");
        if (mode == "Awd" && runtime == "None")
            return Fail("AWD requires a Container or Compose runtime.");
        if (mode == "Awdp" && runtime != "Container")
            return Fail("AWDP only supports a Container runtime.");
        if (mode == "Koh" && runtime != "Container")
            return Fail("KoH requires a Container runtime.");
        var branch = $"{direction}/{slug}";
        var directory = Path.Combine(root, direction, slug);
        if (Directory.Exists(directory))
            return Fail($"Challenge directory '{branch}' already exists.");
        if (!args.Contains("--no-publish", StringComparer.Ordinal)
            && !string.IsNullOrWhiteSpace(Run(
                root,
                "git",
                ["ls-remote", "--heads", "origin", $"refs/heads/{branch}"])))
        {
            var existingUrl = Run(
                root,
                "gh",
                ["pr", "list", "--head", branch, "--state", "all", "--json", "url", "--jq", ".[0].url"])
                .Trim();
            Console.WriteLine(JsonSerializer.Serialize(new { branch, pullRequest = existingUrl }));
            return 0;
        }
        var competition = Mapping(LoadYaml(Path.Combine(root, "competition.yml")));
        if (!string.Equals(Scalar(competition, "mode"), mode, StringComparison.OrdinalIgnoreCase))
            return Fail("Challenge mode must match competition mode.");
        if (Sequence(competition, "challenges")
            .Any(item => int.Parse(Scalar(item, "order")) == order))
            return Fail($"Competition challenge order {order} is already in use.");

        Run(root, "git", ["checkout", "-b", branch]);
        Directory.CreateDirectory(directory);
        Directory.CreateDirectory(Path.Combine(directory, "attachments"));
        Directory.CreateDirectory(Path.Combine(directory, "tests"));
        Directory.CreateDirectory(Path.Combine(directory, "solution"));
        var challengeId = Guid.NewGuid();
        var instanceId = Guid.NewGuid();
        await File.WriteAllTextAsync(
            Path.Combine(directory, "statement.md"),
            $"# {title}{Environment.NewLine}{Environment.NewLine}{Field(fields, "Summary")}{Environment.NewLine}");
        await File.WriteAllTextAsync(
            Path.Combine(directory, "challenge.yml"),
            ScaffoldManifest(challengeId, mode, title, direction, runtime));
        await CreateScaffoldRuntimeFilesAsync(directory, mode, runtime);
        AppendCompetitionChallenge(
            Path.Combine(root, "competition.yml"),
            instanceId,
            branch,
            score,
            order,
            mode);
        await File.WriteAllTextAsync(Path.Combine(directory, "tests", ".gitkeep"), "");
        await File.WriteAllTextAsync(Path.Combine(directory, "solution", ".gitkeep"), "");
        await File.WriteAllTextAsync(Path.Combine(directory, "attachments", ".gitkeep"), "");

        Run(root, "git", ["add", branch, "competition.yml"]);
        Run(root, "git", ["commit", "-m", $"Scaffold {branch}"]);
        if (!args.Contains("--no-publish", StringComparer.Ordinal))
        {
            Run(root, "git", ["push", "--set-upstream", "origin", branch]);
            var issueNumber = issue.GetProperty("number").GetInt32().ToString();
            var body = $"Closes #{issueNumber}\n\nChallenge ID: {challengeId}\nCompetition Challenge ID: {instanceId}";
            Run(root, "gh",
            [
                "pr", "create", "--draft",
                "--title", $"[{direction}] {title}",
                "--body", body,
                "--head", branch,
                "--reviewer", owner
            ]);
            Run(root, "gh", ["issue", "comment", issueNumber, "--body", $"{branch}\n\nChallenge ID: {challengeId}\nCompetition Challenge ID: {instanceId}"]);
            Run(root, "gh", ["issue", "edit", issueNumber, "--add-label", "challenge:scaffolded"]);
        }
        Console.WriteLine(JsonSerializer.Serialize(new { branch, challengeId, competitionChallengeId = instanceId }));
        return 0;
    }

}
