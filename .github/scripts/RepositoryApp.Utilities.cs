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
    private static string SafeChildPath(string parent, string relative)
    {
        var fullParent = Path.GetFullPath(parent).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var full = Path.GetFullPath(Path.Combine(parent, relative));
        if (!full.StartsWith(fullParent, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Path '{relative}' escapes its challenge directory.");
        return full;
    }

    private static Dictionary<string, string> ParseIssueForm(string body)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var matches = Regex.Matches(body, @"(?ms)^### (?<name>[^\r\n]+)\r?\n+(?<value>.*?)(?=^### |\z)");
        foreach (Match match in matches)
            result[match.Groups["name"].Value.Trim()] = match.Groups["value"].Value.Trim();
        return result;
    }

    private static string Field(Dictionary<string, string> fields, string name) =>
        fields.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value
            : throw new InvalidOperationException($"Issue field '{name}' is required.");

    private static string NormalizeMode(string value) => value.Trim().ToLowerInvariant() switch
    {
        "ctf" => "Ctf",
        "awd" => "Awd",
        "awdp" => "Awdp",
        "koh" => "Koh",
        _ => throw new InvalidOperationException($"Unsupported mode '{value}'.")
    };

    private static string NormalizeRuntime(string value) => value.Trim().ToLowerInvariant() switch
    {
        "static (no runtime)" => "None",
        "container" => "Container",
        "compose" => "Compose",
        _ => throw new InvalidOperationException($"Unsupported runtime type '{value}'.")
    };

    private static bool IsMode(string value) =>
        value.Equals("Ctf", StringComparison.OrdinalIgnoreCase)
        || value.Equals("Awd", StringComparison.OrdinalIgnoreCase)
        || value.Equals("Awdp", StringComparison.OrdinalIgnoreCase)
        || value.Equals("Koh", StringComparison.OrdinalIgnoreCase);

    private static string NormalizeSegment(string value)
    {
        var normalized = Regex.Replace(value.Trim().ToLowerInvariant(), @"[^a-z0-9]+", "-").Trim('-');
        if (string.IsNullOrWhiteSpace(normalized))
            throw new InvalidOperationException($"'{value}' cannot be normalized to a path segment.");
        return normalized;
    }

    private static string Relative(string root, string path) =>
        NormalizePath(Path.GetRelativePath(root, path));

    private static string NormalizePath(string path) => path.Replace('\\', '/').Trim('/');

    private static bool TextEquals(JsonObject value, string property, string expected) =>
        string.Equals(value[property]?.ToString(), expected, StringComparison.Ordinal);

    private static bool BoolEquals(JsonObject value, string property, bool expected) =>
        value[property] is JsonValue jsonValue
        && (jsonValue.TryGetValue<bool>(out var actual)
            ? actual == expected
            : bool.TryParse(jsonValue.ToString(), out actual) && actual == expected);

    private static bool JsonEquivalent(string? left, string right)
    {
        if (left is null)
            return false;
        return JsonNode.DeepEquals(JsonNode.Parse(left), JsonNode.Parse(right));
    }

    private static bool NullableInstantEquals(JsonNode? current, string? expected)
    {
        if (current is null || current.GetValueKind() == JsonValueKind.Null)
            return expected is null;
        return expected is not null
               && DateTimeOffset.Parse(current.ToString()).Equals(DateTimeOffset.Parse(expected));
    }

    private static string Required(string[] args, string name) =>
        Option(args, name) ?? throw new InvalidOperationException($"{name} is required.");

    private static string RequiredEnvironment(string name) =>
        Environment.GetEnvironmentVariable(name) is { Length: > 0 } value
            ? value
            : throw new InvalidOperationException($"Environment variable {name} is required.");

    private static string? Option(string[] args, string name)
    {
        var index = Array.IndexOf(args, name);
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }

    private static string Run(string workdir, string file, IReadOnlyList<string> args)
    {
        var start = StartInfo(workdir, file, args);
        start.RedirectStandardOutput = true;
        start.RedirectStandardError = true;
        using var process = Process.Start(start) ?? throw new InvalidOperationException($"Could not start {file}.");
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"{file} failed ({process.ExitCode}): {stderr}");
        return stdout;
    }

    private static async Task RunStreamingAsync(
        string workdir,
        string file,
        IReadOnlyList<string> args)
    {
        using var process = Process.Start(StartInfo(workdir, file, args))
            ?? throw new InvalidOperationException($"Could not start {file}.");
        await process.WaitForExitAsync();
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"{file} failed with exit code {process.ExitCode}.");
    }

    private static ProcessStartInfo StartInfo(
        string workdir,
        string file,
        IReadOnlyList<string> args)
    {
        var start = new ProcessStartInfo(file)
        {
            WorkingDirectory = workdir,
            UseShellExecute = false
        };
        foreach (var arg in args)
            start.ArgumentList.Add(arg);
        return start;
    }

    private static int Fail(string message)
    {
        Console.Error.WriteLine(message);
        return 1;
    }

    private sealed record ChallengeDocument(
        Guid Id,
        string Mode,
        string RelativeDirectory,
        string Directory,
        YamlMappingNode Root);

    private sealed record BuildImage(
        string Key,
        string Context,
        string Dockerfile,
        string? Target,
        string[] Platforms);
}
