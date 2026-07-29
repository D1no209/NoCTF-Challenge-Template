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
        var runtimeBlock = (mode, runtime) switch
        {
            ("Ctf", "None") => "",
            ("Ctf", "Container") => ContainerBlock(
                "runtime",
                "PerTeam",
                "  flagSource: PerTeam\n",
                ctfFlag: true),
            ("Ctf", "Compose") => ComposeBlock(includeChecker: false),
            ("Awd", "Container") => ContainerBlock(
                "runtime",
                "PerTeam",
                "  flagSource: AwdRotation\nflagTemplate:\n  header: flag\n  bodyTemplate: \"[TEAMHASH:32]\"\n  leetLiteralText: false\nflagInjection:\n  command: /app/set-flag '${FLAG}'\n  timeoutSeconds: 30\nchecker:\n  job:\n    image:\n      build: checker\n    timeoutSeconds: 30\n",
                includeChecker: true),
            ("Awd", "Compose") => ComposeBlock(includeChecker: true),
            ("Awdp", "Container") => """

                build:
                  images:
                    - key: target
                      context: runtime
                      dockerfile: runtime/Dockerfile
                    - key: checker
                      context: checker
                      dockerfile: checker/Dockerfile
                runtime:
                  allocation: PerTeam
                  definition:
                    kind: Container
                    image:
                      build: target
                    internalPorts: [8080]
                  limits:
                    memoryBytes: 268435456
                    nanoCpus: 500000000
                    pidsLimit: 128
                patch:
                  entrypoint: fix.sh
                  command: []
                  timeoutSeconds: 60
                  readyTimeoutSeconds: 30
                checker:
                  image:
                    build: checker
                  timeoutSeconds: 30
                """,
            ("Koh", "Container") => ContainerBlock(
                "hill",
                "Shared",
                "  controlCheck:\n    protocol: Http\n    containerPort: 8080\n    path: /flag\n"),
            _ => throw new InvalidOperationException(
                $"Runtime type '{runtime}' is not supported for {mode}.")
        };
        return $"""
                apiVersion: gitops.noctf.dev/v1
                kind: ChallengeTemplate
                id: {id}
                mode: {mode}
                title: {title}
                direction: {direction}
                visibility: Private
                statement: statement.md
                attachments: []
                flags: []
                {runtimeBlock}
                """;
    }

    private static string ContainerBlock(
        string imageKey,
        string allocation,
        string extra,
        bool includeChecker = false,
        bool ctfFlag = false) =>
        $"""

        build:
          images:
            - key: {imageKey}
              context: runtime
              dockerfile: runtime/Dockerfile
              platforms: [linux/amd64]
        {(includeChecker ? """
            - key: checker
              context: checker
              dockerfile: checker/Dockerfile
              platforms: [linux/amd64]
        """ : "")}
        runtime:
          allocation: {allocation}
          definition:
            kind: Container
            image:
              build: {imageKey}
        {(ctfFlag ? "    flagEnvironmentVariableName: FLAG" : "")}
          limits:
            memoryBytes: 268435456
            nanoCpus: 500000000
            pidsLimit: 128
          endpoints:
            - name: web
              protocol: Http
              containerPort: 8080
              exposure: Participants
        {extra}
        """;

    private static string ComposeBlock(bool includeChecker) =>
        $"""

        build:
          images:
            - key: web
              context: runtime/web
              dockerfile: runtime/web/Dockerfile
              platforms: [linux/amd64]
        {(includeChecker ? """
            - key: checker
              context: checker
              dockerfile: checker/Dockerfile
              platforms: [linux/amd64]
        """ : "")}
        runtime:
          allocation: PerTeam
          {(includeChecker ? "flagSource: AwdRotation" : "flagSource: PerTeam")}
          definition:
            kind: Compose
            file: runtime/compose.yml
            serviceImages:
              web:
                build: web
        {(!includeChecker ? """
            flagEnvironmentVariables:
              web: FLAG
        """ : "")}
          limits:
            memoryBytes: 268435456
            nanoCpus: 500000000
            pidsLimit: 128
        {(includeChecker ? """
        flagTemplate:
          header: flag
          bodyTemplate: "[TEAMHASH:32]"
          leetLiteralText: false
        flagInjection:
          command: /app/set-flag '${FLAG}'
          timeoutSeconds: 30
          serviceName: web
        checker:
          targetServiceName: web
          job:
            image:
              build: checker
            timeoutSeconds: 30
        """ : "")}
        """;

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
        var text = File.ReadAllText(path).TrimEnd();
        var entry = $"""

              - id: {id}
                challenge: {challenge}
                order: {order}
                baseScore: {baseScore}
                published: false
                hints: []
                rules:
                  schemaVersion: {(mode == "Awd" ? 4 : 1)}
            """;
        File.WriteAllText(path, text + Environment.NewLine + entry + Environment.NewLine);
    }

}
