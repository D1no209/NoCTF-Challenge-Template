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
    private static int GenerateReadme(string root, bool requireInitialized = false)
    {
        var competition = Mapping(LoadYaml(Path.Combine(root, "competition.yml")));
        var initialized = bool.TryParse(
            Scalar(competition, "initialized", "false"),
            out var value) && value;
        if (!initialized && !requireInitialized)
        {
            Console.WriteLine("README generation skipped: repository is not initialized.");
            return 0;
        }
        File.WriteAllText(Path.Combine(root, "README.md"), RenderReadme(root));
        Console.WriteLine("README.md generated.");
        return 0;
    }

    private static string RenderReadme(string root)
    {
        var competition = Mapping(LoadYaml(Path.Combine(root, "competition.yml")));
        var title = Scalar(competition, "title", "NoCTF Competition");
        var description = NullScalar(competition, "description");
        var mode = NormalizeMode(Scalar(competition, "mode"));
        var documents = FindChallenges(root)
            .Select(path => ReadChallenge(root, path))
            .ToDictionary(item => item.RelativeDirectory, StringComparer.OrdinalIgnoreCase);
        var rows = Sequence(competition, "challenges")
            .Select(item =>
            {
                var path = NormalizePath(Scalar(item, "challenge"));
                if (!documents.TryGetValue(path, out var challenge))
                    throw new InvalidOperationException(
                        $"Cannot render README: challenge '{path}' is missing.");
                return new
                {
                    Order = int.Parse(Scalar(item, "order")),
                    Path = path,
                    Title = Scalar(challenge.Root, "title"),
                    Direction = Scalar(challenge.Root, "direction"),
                    Score = long.Parse(Scalar(item, "baseScore")),
                    Published = bool.Parse(Scalar(item, "published", "false"))
                };
            })
            .OrderBy(item => item.Order)
            .ThenBy(item => item.Path, StringComparer.Ordinal)
            .ToArray();

        var builder = new StringBuilder();
        builder.AppendLine($"# {MarkdownInline(title)}").AppendLine();
        if (!string.IsNullOrWhiteSpace(description))
            builder.AppendLine(description.Trim()).AppendLine();
        builder.AppendLine($"- Competition ID: `{Scalar(competition, "competitionId")}`");
        builder.AppendLine($"- Game mode: `{mode}`");
        builder.AppendLine();
        builder.AppendLine("## Challenges").AppendLine();
        builder.AppendLine("| Order | Challenge | Direction | Base score | Published |");
        builder.AppendLine("| ---: | --- | --- | ---: | :---: |");
        foreach (var row in rows)
            builder.AppendLine(
                $"| {row.Order} | [{MarkdownCell(row.Title)}]({row.Path}/) "
                + $"| {MarkdownCell(row.Direction)} | {row.Score} | {(row.Published ? "Yes" : "No")} |");
        if (rows.Length == 0)
            builder.AppendLine("| — | _No challenges yet_ | — | — | — |");

        builder.AppendLine().AppendLine("### Statistics").AppendLine();
        builder.AppendLine("| Direction | Count | Ratio |");
        builder.AppendLine("| --- | ---: | ---: |");
        foreach (var group in rows
                     .GroupBy(item => item.Direction, StringComparer.OrdinalIgnoreCase)
                     .OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase))
        {
            var ratio = rows.Length == 0 ? 0 : (double)group.Count() / rows.Length;
            builder.AppendLine(
                $"| {MarkdownCell(group.Key)} | {group.Count()} "
                + $"| {ratio.ToString("P0", System.Globalization.CultureInfo.InvariantCulture)} |");
        }
        if (rows.Length == 0)
            builder.AppendLine("| — | 0 | 0% |");

        builder.AppendLine();
        builder.AppendLine(
            "_This README is generated from `competition.yml` and challenge manifests. "
            + "See [repository management](docs/repository-management.md) for authoring and GitOps commands._");
        return builder.ToString().Replace("\r\n", "\n", StringComparison.Ordinal);
    }

    private static string MarkdownInline(string value) =>
        value.Replace('\r', ' ').Replace('\n', ' ').Trim();

    private static string MarkdownCell(string value) =>
        MarkdownInline(value).Replace("|", "\\|", StringComparison.Ordinal);
}
