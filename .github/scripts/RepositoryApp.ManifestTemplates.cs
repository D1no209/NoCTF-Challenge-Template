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
    private static string ScaffoldManifest(
        Guid id,
        string mode,
        string title,
        string direction,
        string runtime)
    {
        var body = (mode, runtime) switch
        {
            ("Ctf", "None") => """
                definition:
                  mode: Ctf
                  ctf:
                    interactionKind: FlagSubmission
                """,
            ("Ctf", "Container") => ContainerManifest("Ctf", "runtime", "PerTeam", "PerTeam", "OwnerOnly"),
            ("Ctf", "Compose") => ComposeManifest("Ctf", includeChecker: false),
            ("Awd", "Container") => ContainerManifest("Awd", "runtime", "PerTeam", "AwdRotation", "Participants", includeChecker: true),
            ("Awd", "Compose") => ComposeManifest("Awd", includeChecker: true),
            ("Awdp", "Container") => ContainerManifest("Awdp", "target", "PerTeam", "PerTeam", "OwnerOnly", includeChecker: true),
            ("Koh", "Container") => ContainerManifest("Koh", "hill", "Shared", null, "Participants", controlCheck: true),
            _ => throw new InvalidOperationException(
                $"Runtime type '{runtime}' is not supported for {mode}.")
        };
        return $"""
                apiVersion: gitops.noctf.dev/v2
                kind: ChallengeTemplate
                id: {id}
                mode: {mode}
                title: {JsonSerializer.Serialize(title)}
                direction: {direction}
                visibility: Private
                statement: statement.md
                attachments: []
                flags: []
                {body}
                """;
    }

    private static string ContainerManifest(
        string mode,
        string imageKey,
        string allocation,
        string? flagSource,
        string exposure,
        bool includeChecker = false,
        bool controlCheck = false)
    {
        var modeBranch = mode switch
        {
            "Ctf" => "  ctf:\n    interactionKind: FlagSubmission",
            "Awd" => "  awd:\n    flagInjection:\n      command: /app/set-flag '${FLAG}'\n      timeoutSeconds: 30\n      serviceName: null",
            "Awdp" => "  awdp: {}",
            "Koh" => "  koh: {}",
            _ => throw new InvalidOperationException($"Unsupported mode '{mode}'.")
        };
        var checker = includeChecker
            ? "\n  checker:"
              + "\n    image:"
              + "\n      build: checker"
              + "\n    command: []"
              + "\n    environment: {}"
              + "\n    timeoutSeconds: 30"
              + "\n    targetServiceName: null"
            : "";
        var patch = mode == "Awdp"
            ? "\n  patchEntrypoint: fix.sh"
              + "\n  patchCommand: []"
              + "\n  patchTimeoutSeconds: 60"
              + "\n  readyTimeoutSeconds: 30"
              + "\n  maximumPatchUploadBytes: 67108864"
              + "\n  checkerFixInput: false"
            : "";
        var controlBinding = controlCheck
            ? "\n      - urlTemplate: http://{HOST}:{PORT}/flag"
              + "\n        exposure: OwnerOnly"
              + "\n        containerPort: 8080"
              + "\n        isControlCheck: true"
            : "";
        return $$"""
                build:
                  images:
                    - key: {{imageKey}}
                      context: runtime
                      dockerfile: runtime/Dockerfile
                      platforms: [linux/amd64]
                {{(includeChecker ? "    - key: checker\n      context: checker\n      dockerfile: checker/Dockerfile\n      platforms: [linux/amd64]" : "")}}
                definition:
                  mode: {{mode}}
                {{modeBranch}}
                  runtime:
                    kind: Container
                    allocation: {{allocation}}
                {{(flagSource is null ? "" : $"    flagSource: {flagSource}\n")}}
                    egressPolicy: Isolated
                    limits:
                      memoryBytes: 268435456
                      nanoCpus: 500000000
                      pidsLimit: 128
                    urlBindings:
                      - urlTemplate: http://{HOST}:{PORT}/
                        exposure: {{exposure}}
                        containerPort: 8080
                        isControlCheck: false{{controlBinding}}
                    container:
                      image:
                        build: {{imageKey}}
                      command: []
                      environment: {}
                      labels: {}
                      portMappings:
                        - containerPort: 8080
                          hostPort: 0
                      security:
                        noNewPrivileges: false
                        readonlyRootfs: false
                        runAsNonRoot: false
                        capDrop: []
                        capAdd: []
                      flagEnvironmentVariableName: {{(mode is "Ctf" or "Awdp" ? "FLAG" : "null")}}
                      internalPorts: {{(mode == "Awdp" ? "[8080]" : "[]")}}{{patch}}{{checker}}
                """;
    }

    private static string ComposeManifest(string mode, bool includeChecker)
    {
        var branch = includeChecker
            ? "  awd:\n    flagInjection:\n      command: /app/set-flag '${FLAG}'\n      timeoutSeconds: 30\n      serviceName: web"
            : "  ctf:\n    interactionKind: FlagSubmission";
        return $$"""
                build:
                  images:
                    - key: web
                      context: runtime/web
                      dockerfile: runtime/web/Dockerfile
                      platforms: [linux/amd64]
                {{(includeChecker ? "    - key: checker\n      context: checker\n      dockerfile: checker/Dockerfile\n      platforms: [linux/amd64]" : "")}}
                definition:
                  mode: {{mode}}
                {{branch}}
                  runtime:
                    kind: Compose
                    allocation: PerTeam
                    flagSource: {{(includeChecker ? "AwdRotation" : "PerTeam")}}
                    egressPolicy: Isolated
                    limits:
                      memoryBytes: 268435456
                      nanoCpus: 500000000
                      pidsLimit: 128
                    urlBindings:
                      - urlTemplate: http://{HOST}:{PORT}/
                        exposure: {{(includeChecker ? "Participants" : "OwnerOnly")}}
                        containerPort: 8080
                        serviceName: web
                        isControlCheck: false
                    compose:
                      file: runtime/compose.yml
                      serviceImages:
                        web:
                          build: web
                      environment: {}
                      labels: {}
                      flagEnvironmentVariables: {{(includeChecker ? "{}" : "{ web: FLAG }")}}
                      serviceResources:
                        - serviceName: web
                          limits:
                            memoryBytes: 268435456
                            nanoCpus: 500000000
                            pidsLimit: 128
                {{(includeChecker ? "  checker:\n    image:\n      build: checker\n    command: []\n    environment: {}\n    timeoutSeconds: 30\n    targetServiceName: web" : "")}}
                """;
    }

    private static async Task CreateScaffoldRuntimeFilesAsync(
        string directory,
        string mode,
        string runtime)
    {
        if (runtime == "None")
            return;
        var runtimeDirectory = Path.Combine(directory, "runtime");
        Directory.CreateDirectory(runtimeDirectory);
        if (runtime == "Compose")
        {
            Directory.CreateDirectory(Path.Combine(runtimeDirectory, "web"));
            await File.WriteAllTextAsync(
                Path.Combine(runtimeDirectory, "compose.yml"),
                "services:\n  web:\n    image: placeholder\n");
            await File.WriteAllTextAsync(
                Path.Combine(runtimeDirectory, "web", "Dockerfile"),
                "FROM busybox:1.37\nCMD [\"httpd\", \"-f\", \"-p\", \"8080\"]\n");
        }
        else
        {
            await File.WriteAllTextAsync(
                Path.Combine(runtimeDirectory, "Dockerfile"),
                "FROM busybox:1.37\nCMD [\"httpd\", \"-f\", \"-p\", \"8080\"]\n");
        }
        if (mode is "Awd" or "Awdp")
        {
            var checkerDirectory = Path.Combine(directory, "checker");
            Directory.CreateDirectory(checkerDirectory);
            await File.WriteAllTextAsync(
                Path.Combine(checkerDirectory, "Dockerfile"),
                "FROM busybox:1.37\nCMD [\"true\"]\n");
        }
    }

    private static void AppendCompetitionChallenge(
        string path,
        Guid id,
        string challenge,
        long baseScore,
        int order,
        string mode)
    {
        var competition = Mapping(LoadYaml(path));
        var entry = $"""
            id: {id}
            challenge: {challenge}
            customTitle: null
            order: {order}
            published: false
            hints: []
            """;
        var node = Mapping(LoadYamlText(entry));
        node.Add("rules", LoadYamlText(DefaultRules(mode, baseScore).ToJsonString()));
        ((YamlSequenceNode)competition.Children[new YamlScalarNode("challenges")]).Add(node);
        using var writer = new StringWriter();
        new YamlStream(new YamlDocument(competition)).Save(writer, assignAnchors: false);
        File.WriteAllText(path, writer.ToString());
    }

    private static JsonObject DefaultRules(string mode, long score)
    {
        JsonObject Curve() => new()
        {
            ["initialPoints"] = score,
            ["minimumPoints"] = score,
            ["decayTeamCount"] = 10,
            ["decayMode"] = "Linear"
        };
        return NormalizeMode(mode) switch
        {
            "Ctf" => new JsonObject
            {
                ["mode"] = "Ctf",
                ["ctf"] = new JsonObject { ["scoreCurve"] = Curve() }
            },
            "Awd" => new JsonObject
            {
                ["mode"] = "Awd",
                ["awd"] = new JsonObject { ["attackPoints"] = score }
            },
            "Awdp" => new JsonObject
            {
                ["mode"] = "Awdp",
                ["awdp"] = new JsonObject
                {
                    ["breakScoreCurve"] = Curve(),
                    ["fixScoreCurve"] = Curve()
                }
            },
            "Koh" => new JsonObject
            {
                ["mode"] = "Koh",
                ["koh"] = new JsonObject { ["controlPointsPerInterval"] = score }
            },
            _ => throw new InvalidOperationException($"Unsupported mode '{mode}'.")
        };
    }
}
