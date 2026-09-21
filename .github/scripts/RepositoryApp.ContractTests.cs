using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using YamlDotNet.RepresentationModel;

internal static partial class RepositoryApp
{
    // Offline test vectors: placeholder image digests must never be used for Apply.
    private static async Task<int> ExportContractFixturesAsync(string[] args)
    {
        var temporary = Path.Combine(Path.GetTempPath(), "noctf-gitops-fixtures", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporary);
        var fixtures = new JsonArray();
        try
        {
            foreach (var (mode, runtime) in new[] { ("Ctf", "None"), ("Ctf", "Container"), ("Ctf", "Compose"),
                ("Awd", "Container"), ("Awd", "Compose"), ("Awdp", "Container"), ("Koh", "Container") })
            {
                var directory = Path.Combine(temporary, "web", mode.ToLowerInvariant()+"-"+runtime.ToLowerInvariant());
                Directory.CreateDirectory(directory);
                await CreateScaffoldRuntimeFilesAsync(directory, mode, runtime);
                var path = Path.Combine(directory, "challenge.yml");
                await File.WriteAllTextAsync(path, ScaffoldManifest(Guid.NewGuid(), mode, "Contract fixture", "Web", runtime));
                var definition = MaterializeDefinition(ReadChallenge(temporary, path),
                    _ => "example.invalid/test@sha256:" + new string('1', 64));
                fixtures.Add(new JsonObject
                {
                    ["scenario"] = mode+"/"+runtime,
                    ["mode"] = mode,
                    ["definition"] = JsonNode.Parse(definition),
                    ["rules"] = DefaultRules(mode, 100)
                });
            }
            var awdp = fixtures.OfType<JsonObject>().Single(item => item["mode"]!.ToString() == "Awdp").DeepClone().AsObject();
            awdp["scenario"] = "Awdp/CheckerFixInput";
            awdp["definition"]!["checkerFixInput"] = true;
            fixtures.Add(awdp);
            var json = fixtures.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true })+"\n";
            var output = Option(args, "--output");
            if (output is null) Console.WriteLine(json);
            else await File.WriteAllTextAsync(output, json);
            return 0;
        }
        finally { Directory.Delete(temporary, recursive: true); }
    }

    private static async Task ContractTestsAsync()
    {
        await AuditRegressionTestsAsync();
        var requests = new List<string>();
        using (var client = new NoCtfClient("https://noctf.example", "test-token",
            new ContractHandler(request =>
            {
                requests.Add(request.RequestUri!.PathAndQuery);
                return JsonResponse("""{"items":[{"id":"first"}]}""");
            })))
        {
            var items = await client.GetItemsAsync("/api/v1/admin/challenges/00000000-0000-0000-0000-000000000001/flags?includeDeleted=true");
            Check(items.Count == 1 && requests.Count == 1 && requests[0].Contains("includeDeleted=true"),
                "GitOps collection reads must preserve filters without inventing pagination.");
        }

        var attempts = 0;
        using (var client = new NoCtfClient("https://noctf.example", "test-token",
            new ContractHandler(_ =>
            {
                attempts++;
                return JsonResponse("""{"code":"ActiveRuntimeDefinitionConflict","detail":"flag{do-not-log} eyJhbGci.private.signature"}""",
                    HttpStatusCode.Conflict);
            })))
        {
            try { await client.SendAsync(HttpMethod.Patch, "/api/v1/challenge", new { title = "safe" }); throw new Exception("Expected conflict."); }
            catch (NoCtfApiException error)
            {
                Check(error.Code == "ActiveRuntimeDefinitionConflict" && !error.Message.Contains("flag{")
                    && !error.Message.Contains("eyJhbGci") && attempts == 1,
                    "Business conflicts must not be retried or expose response secrets.");
            }
        }

        attempts = 0;
        using (var client = new NoCtfClient("https://noctf.example", "test-token",
            new ContractHandler(request =>
            {
                attempts++;
                if (attempts == 1) throw new HttpRequestException("Simulated lost response after commit.");
                return request.Method == HttpMethod.Post
                    ? JsonResponse("""{"code":"ResourceIdConflict"}""", HttpStatusCode.Conflict)
                    : JsonResponse("""{"id":"stable","title":"same"}""");
            })))
        {
            var created = await client.CreateAsync("/api/v1/items", new { id = "stable" },
                "/api/v1/items/stable", item => item["title"]?.ToString() == "same");
            Check(created["id"]?.ToString() == "stable" && attempts == 2,
                "Lost create responses must converge by rereading the stable ID.");
        }

        using (var client = new NoCtfClient("https://noctf.example", "test-token",
            new ContractHandler(request => request.Method == HttpMethod.Post
                ? JsonResponse("{}", HttpStatusCode.Conflict)
                : JsonResponse("""{"title":"different"}"""))))
        {
            try
            {
                await client.CreateAsync("/api/v1/items", new { id = "stable" }, "/api/v1/items/stable",
                    item => item["title"]?.ToString() == "same");
                throw new Exception("Mismatching stable IDs must fail.");
            }
            catch (NoCtfApiException error) when (error.StatusCode == HttpStatusCode.Conflict) { }
        }

        using (var client = new NoCtfClient("https://noctf.example", "test-token",
            new ContractHandler(_ => throw new Exception("An external URL reached the transport."))))
        {
            try { await client.GetAsync("https://attacker.example/api/v1/items"); throw new Exception("External URI was accepted."); }
            catch (InvalidOperationException error) when (error.Message.Contains("relative")) { }
        }
        using (var client = new NoCtfClient("https://noctf.example", "test-token",
            new ContractHandler(request =>
            {
                Check(request.Content?.Headers.ContentType?.MediaType == "application/json",
                    "Restore POST requests must carry an empty JSON body, not cause HTTP 415.");
                return new HttpResponseMessage(HttpStatusCode.NoContent);
            })))
            await client.SendAsync(HttpMethod.Post, "/api/v1/item/restore");

        var yaml = YamlToJson(LoadYamlText("port: 8080\nquoted: '123'\nboolean: \"true\"\n"))!;
        Check(yaml["port"]!.GetValue<int>() == 8080 && yaml["quoted"]!.GetValue<string>() == "123"
            && yaml["boolean"]!.GetValue<string>() == "true", "YAML scalar types must survive materialization.");
        foreach (var mode in new[] { "Ctf", "Awd", "Awdp", "Koh" })
        {
            var rules = DefaultRules(mode, 100);
            Check(rules["schemaVersion"]!.GetValue<int>() == RulesSchemaVersion(mode), "Wrong rules schema.");
            Check(!rules.ContainsKey("baseScore"), "Removed BaseScore must not reappear.");
        }
        Check(DefinitionSchemaVersion("Ctf") == 3 && RulesSchemaVersion("Ctf") == 2,
            "CTF Definition and Rules schema versions must remain independent.");
        try
        {
            RequireCompetitionManagement(new JsonObject
            {
                ["competition"] = new JsonObject { ["deletedAt"] = null },
                ["capabilities"] = new JsonObject { ["canModerate"] = false }
            });
            throw new Exception("Judge was accepted.");
        }
        catch (InvalidOperationException error) when (error.Message.Contains("Manager")) { }
        var competitionResponse = new JsonObject
        {
            ["competition"] = new JsonObject
            {
                ["mode"] = "Ctf",
                ["status"] = "Draft",
                ["title"] = "Contract competition",
                ["deletedAt"] = null
            },
            ["capabilities"] = new JsonObject { ["canModerate"] = true }
        };
        RequireCompetitionManagement(competitionResponse);
        Check(CompetitionResource(competitionResponse)["title"]!.ToString() == "Contract competition",
            "Competition reads must unwrap the current aggregate response.");

        using (var client = new NoCtfClient("https://noctf.example", "test-token",
            new ContractHandler(request =>
            {
                Check(request.Method == HttpMethod.Patch,
                    "Challenge templates must use PATCH instead of the removed PUT contract.");
                var body = JsonNode.Parse(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult())!;
                Check(body["content"]?["title"]?.ToString() == "Current contract"
                      && body["mode"] is null,
                    "Challenge template updates must use the content section.");
                return JsonResponse("""{"title":"Current contract"}""");
            })))
        {
            var challenge = new ChallengeDocument(
                Guid.NewGuid(),
                "Ctf",
                "web/current-contract",
                "",
                new YamlMappingNode
                {
                    { "title", "Current contract" },
                    { "direction", "Web" }
                });
            await UpdateChallengeMetadataAsync(
                client,
                challenge,
                new JsonObject(),
                "{\"schemaVersion\":3}",
                "Statement",
                "Private");
        }

        using (var client = new NoCtfClient("https://noctf.example", "test-token",
            new ContractHandler(_ => JsonResponse("""{"deliveryPolicy":"RandomOnePerTeam","items":[]}"""))))
        {
            try
            {
                await GetManagedAttachmentsAsync(client, Guid.NewGuid());
                throw new Exception("RandomOnePerTeam attachments were accepted.");
            }
            catch (InvalidOperationException error) when (error.Message.Contains("RandomOnePerTeam")) { }
        }

        var temporary = Path.Combine(Path.GetTempPath(), "noctf-gitops-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporary);
        try
        {
            var path = Path.Combine(temporary, "competition.yml");
            File.WriteAllText(path, "mode: Ctf\nchallenges: []\n");
            AppendCompetitionChallenge(path, Guid.NewGuid(), "web/test", 100, 1, "Ctf");
            Check(Sequence(Mapping(LoadYaml(path)), "challenges").Count == 1,
                "Scaffolding must work after initialization with an empty challenges list.");
            var directory = Path.Combine(temporary, "web", "test");
            Directory.CreateDirectory(directory);
            await CreateScaffoldRuntimeFilesAsync(directory, "Awdp", "Container");
            var manifestPath = Path.Combine(directory, "challenge.yml");
            File.WriteAllText(manifestPath, ScaffoldManifest(Guid.NewGuid(), "Awdp", "Plan test", "Web", "Container"));
            Run(temporary, "git", ["init", "--quiet"]);
            Run(temporary, "git", ["add", "."]);
            Run(temporary, "git", ["-c", "user.name=Contract Test", "-c", "user.email=contract@example.invalid", "commit", "--quiet", "-m", "fixture"]);
            var document = ReadChallenge(temporary, manifestPath);
            var target = BuildImages(document.Root).Single(image => image.Key == "target");
            var checker = BuildImages(document.Root).Single(image => image.Key == "checker");
            Check(!ImageNeedsBuild(temporary, document, target, "HEAD", ["web/test/statement.md"]),
                "Statement edits must not rebuild an image.");
            Check(ImageNeedsBuild(temporary, document, target, "HEAD", ["web/test/runtime/Dockerfile"])
                && !ImageNeedsBuild(temporary, document, checker, "HEAD", ["web/test/runtime/Dockerfile"]),
                "A Runtime change must not rebuild the independent Checker.");
            Check(!ImageNeedsBuild(temporary, document, target, "HEAD", ["web/test/challenge.yml"])
                && ImageNeedsBuild(temporary, document, target with { Target = "new-stage" }, "HEAD", ["web/test/challenge.yml"]),
                "Manifest changes must distinguish metadata from build configuration.");
        }
        finally
        {
            // Git object files are read-only on Windows; this directory belongs only to this test.
            foreach (var file in new DirectoryInfo(temporary).EnumerateFiles("*", SearchOption.AllDirectories))
                file.IsReadOnly = false;
            Directory.Delete(temporary, recursive: true);
        }
        Console.WriteLine("GitOps contract tests passed (collections, retries, redaction, permissions, schemas, empty scaffold, incremental builds).");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static HttpResponseMessage JsonResponse(string json, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json") };

    private sealed class ContractHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var response = respond(request);
            response.RequestMessage = request;
            return Task.FromResult(response);
        }
    }
}
