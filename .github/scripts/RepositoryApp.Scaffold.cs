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
        var eventRoot = LoadIssueEvent(root, args);
        var issue = eventRoot.GetProperty("issue");
        var issueNumber = issue.GetProperty("number").GetInt32();
        if (issue.GetProperty("state").GetString() != "open"
            || !issue.GetProperty("title").GetString()!.StartsWith("[Challenge]", StringComparison.Ordinal))
            return Fail("Scaffolding requires an open Challenge Issue.");
        var permission = ResolveCurrentRepositoryPermission(root, eventRoot);
        if (!HasRepositoryWritePermission(permission))
            return Fail("Issue author needs write or administrator repository permission to scaffold challenges.");
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
        if (!args.Contains("--no-publish", StringComparer.Ordinal)
            && !string.IsNullOrWhiteSpace(Run(root, "git",
                ["ls-remote", "--heads", "origin", $"refs/heads/{branch}"])))
        {
            Run(root, "git", ["fetch", "origin", $"refs/heads/{branch}:refs/remotes/origin/{branch}"]);
            Run(root, "git", ["checkout", "-B", branch, $"origin/{branch}"]);
            var existingChallenge = ReadChallenge(root, Path.Combine(directory, "challenge.yml"));
            var existingCompetition = Mapping(LoadYaml(Path.Combine(root, "competition.yml")));
            var existingEntry = Sequence(existingCompetition, "challenges").SingleOrDefault(item =>
                NormalizePath(Scalar(item, "challenge")) == branch);
            if (existingEntry is null)
                throw new InvalidOperationException($"Branch {branch} has no matching competition challenge.");
            var existingInstanceId = Guid.Parse(Scalar(existingEntry, "id"));
            var existingUrl = EnsureChallengePullRequest(root, branch, title, direction, owner,
                issueNumber, existingChallenge.Id, existingInstanceId);
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                branch, challengeId = existingChallenge.Id,
                competitionChallengeId = existingInstanceId, pullRequest = existingUrl
            }));
            return 0;
        }
        if (Directory.Exists(directory))
            return Fail($"Challenge directory '{branch}' already exists.");
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
            var pullRequest = EnsureChallengePullRequest(root, branch, title, direction, owner,
                issueNumber, challengeId, instanceId);
            Run(root, "gh", ["issue", "comment", issueNumber.ToString(), "--body",
                $"Draft PR: {pullRequest}\n\nChallenge ID: {challengeId}\nCompetition Challenge ID: {instanceId}"]);
        }
        Console.WriteLine(JsonSerializer.Serialize(new { branch, challengeId, competitionChallengeId = instanceId }));
        return 0;
    }

    private static string EnsureChallengePullRequest(string root, string branch, string title,
        string direction, string reviewer, int issueNumber, Guid challengeId, Guid instanceId)
    {
        var existing = Run(root, "gh",
            ["pr", "list", "--head", branch, "--state", "all", "--json", "url,body,state", "--jq", ".[0]"])
            .Trim();
        if (existing.Length > 0)
        {
            var pullRequest = JsonNode.Parse(existing)!.AsObject();
            if (!pullRequest["body"]!.ToString().Contains($"Closes #{issueNumber}", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"The existing PR for {branch} is linked to another Issue.");
            if (pullRequest["state"]!.ToString() != "OPEN")
                throw new InvalidOperationException($"The existing PR for {branch} is no longer open.");
            return pullRequest["url"]!.GetValue<string>();
        }

        var body = $"Closes #{issueNumber}\n\nChallenge ID: {challengeId}\nCompetition Challenge ID: {instanceId}";
        return Run(root, "gh",
        [
            "pr", "create", "--draft",
            "--base", "main",
            "--title", $"[{direction}] {title}",
            "--body", body,
            "--head", branch,
            "--reviewer", reviewer
        ]).Trim();
    }

}
