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
    private static async Task<int> InitializeAsync(string root, string[] args)
    {
        var issueNumber = 0;
        try
        {
            var eventRoot = LoadIssueEvent(root, args);
            var issue = eventRoot.GetProperty("issue");
            issueNumber = issue.GetProperty("number").GetInt32();
            var permission = ResolveCurrentRepositoryPermission(root, eventRoot);
            if (!HasRepositoryWritePermission(permission))
                throw new InvalidOperationException(
                    "Only users with write or administrator repository permission may initialize a competition.");

            var fields = ParseIssueForm(issue.GetProperty("body").GetString() ?? "");
            var competitionId = Guid.Parse(Field(fields, "Competition ID"));
            var requestedMode = NormalizeMode(Field(fields, "Game Mode"));
            var apiUrl = Environment.GetEnvironmentVariable("NOCTF_API_URL");
            if (string.IsNullOrWhiteSpace(apiUrl))
                throw new InvalidOperationException(
                    "Repository variable NOCTF_API_URL is missing.");
            var token = Environment.GetEnvironmentVariable("NOCTF_BOT_TOKEN");
            if (string.IsNullOrWhiteSpace(token))
                throw new InvalidOperationException(
                    "Repository secret NOCTF_BOT_TOKEN is missing.");

            using var client = new NoCtfClient(apiUrl, token);
            var competition = await client.GetAsync(
                $"/api/v1/admin/competitions/{competitionId}",
                allowNotFound: true);
            if (competition is null)
                throw new InvalidOperationException(
                    "The competition was not found or the Bot JWT cannot access it. "
                    + "Grant the Bot competition Manager permission and retry.");
            RequireCompetitionManagement(competition);
            var competitionResource = CompetitionResource(competition);
            var actualMode = NormalizeApiMode(competitionResource["mode"]?.ToString() ?? "");
            if (!actualMode.Equals(requestedMode, StringComparison.Ordinal))
                throw new InvalidOperationException(
                    $"Mode mismatch: the Issue requests {requestedMode}, "
                    + $"but NoCTF reports {actualMode}.");

            var current = Mapping(LoadYaml(Path.Combine(root, "competition.yml")));
            if (bool.TryParse(Scalar(current, "initialized", "false"), out var initialized)
                && initialized)
            {
                var currentId = Guid.Parse(Scalar(current, "competitionId"));
                if (currentId != competitionId)
                    throw new InvalidOperationException(
                        $"This repository is already initialized for competition {currentId}.");
                await PublishInitializationStatusAsync(
                    root,
                    issueNumber,
                    true,
                    "This repository is already initialized for the requested competition.");
                return 0;
            }

            const string branch = "initialize/competition";
            Run(root, "git", ["fetch", "origin", "main"]);
            if (!string.IsNullOrWhiteSpace(
                    Run(root, "git", ["ls-remote", "--heads", "origin", $"refs/heads/{branch}"])))
                Run(
                    root,
                    "git",
                    ["fetch", "origin", $"+refs/heads/{branch}:refs/remotes/origin/{branch}"]);
            Run(root, "git", ["checkout", "-B", branch, "origin/main"]);
            WriteInitializedCompetition(
                Path.Combine(root, "competition.yml"),
                competitionId,
                competitionResource["title"]?.ToString() ?? "NoCTF Competition",
                competitionResource["description"]?.GetValue<string?>(),
                actualMode);
            var examples = SafeChildPath(root, "examples");
            if (Directory.Exists(examples))
                Directory.Delete(examples, recursive: true);
            GenerateReadme(root, requireInitialized: true);
            Run(root, "git", ["add", "--all"]);
            if (!string.IsNullOrWhiteSpace(Run(root, "git", ["status", "--porcelain"])))
                Run(root, "git", ["commit", "-m", $"Initialize competition {competitionId}"]);

            var pullRequest = "";
            if (!args.Contains("--no-publish", StringComparer.Ordinal))
            {
                Run(root, "git", ["push", "--force-with-lease", "--set-upstream", "origin", branch]);
                pullRequest = Run(
                        root,
                        "gh",
                        ["pr", "list", "--head", branch, "--state", "open", "--json", "url", "--jq", ".[0].url"])
                    .Trim();
                if (string.IsNullOrWhiteSpace(pullRequest))
                {
                    pullRequest = Run(
                            root,
                            "gh",
                            [
                                "pr", "create", "--draft",
                                "--title", "Initialize NoCTF competition repository",
                                "--body", $"Closes #{issueNumber}\n\n"
                                          + $"Competition ID: {competitionId}\nMode: {actualMode}",
                                "--head", branch
                            ])
                        .Trim();
                }
            }

            var message = "NoCTF connectivity, JWT authentication, competition access, ID, and Mode checks passed. "
                          + "The Bot must retain Manager permission for later Apply operations.";
            if (!string.IsNullOrWhiteSpace(pullRequest))
                message += $"\n\nDraft initialization PR: {pullRequest}";
            await PublishInitializationStatusAsync(root, issueNumber, true, message);
            return 0;
        }
        catch (Exception exception)
        {
            if (issueNumber != 0)
                await PublishInitializationStatusAsync(
                    root,
                    issueNumber,
                    false,
                    exception.Message + "\n\nFix the configuration, then comment `/retry` on this Issue.");
            Console.Error.WriteLine(exception.Message);
            return 1;
        }
    }

    private static JsonElement LoadIssueEvent(string root, string[] args)
    {
        var issueOption = Option(args, "--issue");
        var json = issueOption is null
            ? File.ReadAllText(Required(args, "--event"))
            : $$"""{"issue":{{Run(
                root,
                "gh",
                ["api", $"repos/{RequiredEnvironment("GITHUB_REPOSITORY")}/issues/{int.Parse(issueOption)}"])}}}""";
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private static string? ResolveCurrentRepositoryPermission(
        string root,
        JsonElement eventRoot)
    {
        var repository = RequiredEnvironment("GITHUB_REPOSITORY");
        var actor = eventRoot.TryGetProperty("comment", out var comment)
            ? comment.GetProperty("user").GetProperty("login").GetString()
            : eventRoot.GetProperty("issue").GetProperty("user").GetProperty("login").GetString();
        if (string.IsNullOrWhiteSpace(actor))
            return null;
        var path = $"repos/{repository}/collaborators/{actor}/permission";
        using var current = JsonDocument.Parse(Run(root, "gh", ["api", path]));
        return current.RootElement.GetProperty("permission").GetString();
    }

    private static bool HasRepositoryWritePermission(string? permission) =>
        permission is "admin" or "write";

    private static string NormalizeApiMode(string value) => value.Trim().ToLowerInvariant() switch
    {
        "0" or "ctf" => "Ctf",
        "1" or "awd" => "Awd",
        "2" or "awdp" => "Awdp",
        "3" or "koh" => "Koh",
        _ => throw new InvalidOperationException($"NoCTF returned unsupported mode '{value}'.")
    };

    private static void WriteInitializedCompetition(
        string path,
        Guid competitionId,
        string title,
        string? description,
        string mode)
    {
        var descriptionYaml = description is null ? "null" : JsonSerializer.Serialize(description);
        File.WriteAllText(
            path,
            $"""
             apiVersion: gitops.noctf.dev/v2
             kind: CompetitionChallengeSet
             initialized: true
             competitionId: {competitionId}
             title: {JsonSerializer.Serialize(title)}
             description: {descriptionYaml}
             mode: {mode}
             challenges: []
             """);
    }

    private static async Task PublishInitializationStatusAsync(
        string root,
        int issueNumber,
        bool succeeded,
        string detail)
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("GH_TOKEN")))
            return;
        var marker = "<!-- noctf-initialize-status -->";
        var icon = succeeded ? "✅" : "❌";
        var body = $"{marker}\n## {icon} NoCTF competition initialization\n\n{detail}";
        try
        {
            var repository = RequiredEnvironment("GITHUB_REPOSITORY");
            var commentId = Run(
                    root,
                    "gh",
                    [
                        "api", $"repos/{repository}/issues/{issueNumber}/comments",
                        "--paginate",
                        "--jq", $".[] | select(.body | contains(\"{marker}\")) | .id"
                    ])
                .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
                .LastOrDefault();
            if (commentId is null)
                Run(root, "gh", ["issue", "comment", issueNumber.ToString(), "--body", body]);
            else
                Run(
                    root,
                    "gh",
                    [
                        "api", "--method", "PATCH",
                        $"repos/{repository}/issues/comments/{commentId}",
                        "-f", $"body={body}"
                    ]);
        }
        catch (Exception commentException)
        {
            Console.Error.WriteLine($"Could not publish initialization status: {commentException.Message}");
        }
        await Task.CompletedTask;
    }
}
