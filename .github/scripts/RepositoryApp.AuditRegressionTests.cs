using System.Net;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using YamlDotNet.RepresentationModel;

internal static partial class RepositoryApp
{
    private static async Task AuditRegressionTestsAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "noctf-gitops-regressions", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            SourceHashRegression(root);
            await ImageReferenceRegressionAsync(root);
            HintValidationRegression(root);
            await AttachmentRecoveryRegressionAsync(root);
            await PublishedFailureRegressionAsync();
            await RestoreOrderRegressionAsync(false);
            await RestoreOrderRegressionAsync(true);
        }
        finally
        {
            foreach (var file in new DirectoryInfo(root).EnumerateFiles("*", SearchOption.AllDirectories))
                file.IsReadOnly = false;
            Directory.Delete(root, recursive: true);
        }
        Console.WriteLine("Audit regressions passed: framed hashes, immutable image references, attachment identity, publication safety, restore collisions and interrupted recovery.");
    }

    private static void HintValidationRegression(string root)
    {
        var directory = Path.Combine(root, "web", "hints");
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "statement.md"), "hint validation");
        File.WriteAllText(Path.Combine(directory, "challenge.yml"), ScaffoldManifest(Guid.NewGuid(), "Ctf", "Hints", "Web", "None"));
        foreach (var (content, publishedAt, valid) in new[]
        {
            ("''", "null", false), ("'valid hint'", "'invalid-date'", false),
            ("'valid hint'", "'2026-09-01T00:00:00Z'", true)
        })
        {
            File.WriteAllText(Path.Combine(root, "competition.yml"), $$"""
                apiVersion: gitops.noctf.dev/v2
                competitionId: {{Guid.NewGuid()}}
                mode: Ctf
                challenges:
                  - id: {{Guid.NewGuid()}}
                    challenge: web/hints
                    order: 1
                    published: true
                    rules: { mode: Ctf, ctf: {} }
                    hints:
                      - id: {{Guid.NewGuid()}}
                        content: {{content}}
                        cost: 0
                        publishedAt: {{publishedAt}}
                """);
            Check((ValidateRepository(root).Count == 0) == valid,
                "Empty hint content and malformed timestamps must fail before any API write.");
        }
    }

    private static void SourceHashRegression(string root)
    {
        foreach (var name in new[] { "left", "right" })
        {
            Directory.CreateDirectory(Path.Combine(root, name, "runtime"));
            File.WriteAllText(Path.Combine(root, name, "runtime", "Dockerfile"), "FROM scratch\n");
        }
        File.WriteAllText(Path.Combine(root, "left", "runtime", "a"), "bc");
        File.WriteAllText(Path.Combine(root, "right", "runtime", "ab"), "c");
        var image = new BuildImage("runtime", "runtime", "runtime/Dockerfile", null, ["linux/amd64"]);
        var left = Path.Combine(root, "left");
        var first = SourceHash(left, image);
        Check(first != SourceHash(Path.Combine(root, "right"), image), "File paths and contents need distinct hash boundaries.");
        Check(first == SourceHash(left, image), "Repeated source hashing must be deterministic.");
        Run(left, "git", ["init", "--quiet"]);
        Run(left, "git", ["add", "."]);
        var beforeMode = SourceHash(left, image);
        Run(left, "git", ["update-index", "--chmod=+x", "runtime/a"]);
        Check(beforeMode != SourceHash(left, image), "Git executable modes must participate in source identity.");
    }

    private static async Task ImageReferenceRegressionAsync(string root)
    {
        const string pinned = "example.invalid/target@sha256:1111111111111111111111111111111111111111111111111111111111111111";
        foreach (var (mode, kind) in new[] { ("Ctf", "Container"), ("Awdp", "Container"), ("Awd", "Compose") })
        {
            var directory = Path.Combine(root, "web", mode.ToLowerInvariant());
            Directory.CreateDirectory(directory);
            await CreateScaffoldRuntimeFilesAsync(directory, mode, kind);
            File.WriteAllText(Path.Combine(directory, "statement.md"), "test");
            var manifest = Mapping(LoadYamlText(ScaffoldManifest(Guid.NewGuid(), mode, "test", "Web", kind)));
            var definition = Mapping(manifest.Children[new YamlScalarNode("definition")]);
            var runtime = Mapping(definition.Children[new YamlScalarNode("runtime")]);
            var payload = Mapping(runtime.Children[new YamlScalarNode(kind.ToLowerInvariant())]);
            var container = kind == "Compose"
                ? Mapping(payload.Children[new YamlScalarNode("serviceImages")])
                : payload;
            var key = new YamlScalarNode(kind == "Compose" ? "web" : "image");
            foreach (var invalid in new YamlNode[]
            {
                new YamlScalarNode("nginx:latest"),
                new YamlMappingNode { { "external", "nginx:latest" } },
                new YamlMappingNode { { "external", pinned }, { "build", "runtime" } },
                new YamlMappingNode { { "build", "missing" } }
            })
            {
                container.Children[key] = invalid;
                var document = new ChallengeDocument(Guid.NewGuid(), mode, "web/" + mode.ToLowerInvariant(), directory, manifest);
                var errors = new List<string>();
                ValidateChallenge(document, mode, [], [], [], errors);
                Check(errors.Count > 0, "Every image location must reject ambiguous, mutable or unknown references.");
                try
                {
                    _ = MaterializeDefinition(document, _ => pinned);
                    throw new Exception("Materialization accepted an invalid image reference.");
                }
                catch (InvalidOperationException) { }
            }
            container.Children[key] = new YamlMappingNode { { "external", pinned } };
            payload.Children[new YamlScalarNode("environment")] = new YamlMappingNode { { "external", "ordinary data" } };
            var valid = new ChallengeDocument(Guid.NewGuid(), mode, "web/" + mode.ToLowerInvariant(), directory, manifest);
            var validErrors = new List<string>();
            ValidateChallenge(valid, mode, [], [], [], validErrors);
            Check(validErrors.Count == 0,
                "Pinned images must be accepted: " + string.Join(" | ", validErrors));
            var materialized = MaterializeDefinition(valid, _ => pinned);
            Check(materialized["runtime"]![kind.ToLowerInvariant()]!["environment"]!["external"]!.GetValue<string>() == "ordinary data",
                "Image replacement must not rewrite environment dictionaries.");
        }
    }

    private static async Task AttachmentRecoveryRegressionAsync(string root)
    {
        var path = Path.Combine(root, "handout.txt");
        File.WriteAllText(path, "same");
        var id = Guid.NewGuid();
        var hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
        foreach (var (name, contentType, length, success) in new[]
        {
            ("handout.txt", "text/plain", 4L, true), ("other.zip", "text/plain", 4L, false),
            ("handout.txt", "application/zip", 4L, false), ("handout.txt", "text/plain", 5L, false)
        })
        {
            using var client = new NoCtfClient("https://noctf.example", "test-token", new ContractHandler(request =>
            {
                if (request.Method == HttpMethod.Post)
                {
                    var body = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
                    Check(request.Content.Headers.ContentType?.MediaType == "multipart/form-data"
                          && body.Contains("DeliveryPolicy", StringComparison.Ordinal)
                          && body.Contains("AttachmentIds", StringComparison.Ordinal)
                          && body.Contains("Files", StringComparison.Ordinal),
                        "Attachment uploads must use the current batch multipart contract.");
                    return JsonResponse("{}", HttpStatusCode.Conflict);
                }
                return JsonResponse(new JsonObject { ["items"] = new JsonArray(new JsonObject
                { ["id"] = id, ["sha256"] = hash, ["fileName"] = name, ["contentType"] = contentType,
                    ["byteLength"] = length, ["deletedAt"] = null }) }.ToJsonString());
            }));
            try
            {
                await client.UploadAsync("/api/v1/items", id, path, "text/plain");
                Check(success, "A conflicting attachment's metadata must not be accepted just because its bytes match.");
            }
            catch (NoCtfApiException) when (!success) { }
        }
    }

    private static async Task PublishedFailureRegressionAsync()
    {
        var id = Guid.NewGuid(); var template = Guid.NewGuid(); var competition = Guid.NewGuid();
        foreach (var initialPublished in new[] { true, false })
        {
            var published = initialPublished;
            var manifest = Mapping(LoadYamlText($$"""
                challenges:
                  - id: {{id}}
                    challenge: web/one
                    order: 10
                    published: true
                    rules: { mode: Ctf, ctf: {} }
                    hints: [{ id: '00000000-0000-0000-0000-000000000144', content: Valid hint, cost: 0, publishedAt: null }]
                """));
            using var client = new NoCtfClient("https://noctf.example", "test-token", new RegressionHandler(async request =>
            {
                var path = request.RequestUri!.AbsolutePath;
                if (request.Method == HttpMethod.Get && path.EndsWith("/hints")) return JsonResponse("""{"items":[]}""");
                if (request.Method == HttpMethod.Get && path.Contains("/hints/")) return JsonResponse("{}", HttpStatusCode.NotFound);
                if (request.Method == HttpMethod.Get && path.EndsWith("/challenges"))
                    return JsonResponse(new JsonObject { ["items"] = new JsonArray(RegressionRow(id, template, 10, published, false)) }.ToJsonString());
                if (request.Method == HttpMethod.Get)
                    return JsonResponse(RegressionDetail(RegressionRow(id, template, 10, published, false)).ToJsonString());
                if (request.Method == HttpMethod.Patch)
                {
                    var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync())!;
                    var presentation = body["presentation"];
                    if (body["rules"] is JsonNode rules)
                        Check(rules["configuration"]?["mode"]?.ToString() == "Ctf"
                            && rules["json"] is null,
                            "Competition challenge PATCH must use rules.configuration.");
                    if (presentation is not null)
                        published = presentation["isPublished"]!.GetValue<bool>();
                    return JsonResponse(RegressionDetail(RegressionRow(id, template, 10, published, false)).ToJsonString());
                }
                if (request.Method == HttpMethod.Post && path.EndsWith("/hints")) return JsonResponse("{}", HttpStatusCode.InternalServerError);
                throw new InvalidOperationException("Unexpected publication regression request.");
            }));
            try
            {
                await ApplyCompetitionChallengesAsync(client, competition, manifest, new Dictionary<string, ChallengeDocument>
                { ["web/one"] = new(template, "Ctf", "web/one", "", new YamlMappingNode()) });
                throw new Exception("A simulated write failure was ignored.");
            }
            catch (NoCtfApiException) { Check(published == initialPublished, "A failed update must preserve the existing publication state."); }
        }
    }

    private static async Task RestoreOrderRegressionAsync(bool interrupt)
    {
        var ids = new[] { Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid() };
        var templates = new[] { Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid() };
        var rows = ids.Select((id, index) => RegressionRow(id, templates[index], 10, false, index > 0))
            .ToDictionary(row => row["id"]!.GetValue<Guid>());
        var manifest = new YamlMappingNode { { "challenges", new YamlSequenceNode(ids.Select((id, index) => LoadYamlText($$"""
            id: {{id}}
            challenge: web/item-{{index}}
            order: {{(index == 0 ? 10 : 20 + index * 10)}}
            published: false
            hints: []
            rules: { mode: Ctf, ctf: {} }
            """))) } };
        var documents = ids.Select((id, index) => new ChallengeDocument(templates[index], "Ctf", "web/item-"+index, "", new YamlMappingNode()))
            .ToDictionary(document => document.RelativeDirectory);
        var interrupted = false;
        var restoredFirst = false;
        using var client = new NoCtfClient("https://noctf.example", "test-token", new RegressionHandler(async request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (request.Method == HttpMethod.Get && path.EndsWith("/challenges"))
                return JsonResponse(new JsonObject { ["items"] = new JsonArray(rows.Values.Select(row => row.DeepClone()).ToArray()) }.ToJsonString());
            if (path.EndsWith("/hints")) return JsonResponse("""{"items":[]}""");
            var restore = path.EndsWith("/restore");
            var id = Guid.Parse(path.Split('/')[restore ? ^2 : ^1]);
            var row = rows[id];
            if (restore)
            {
                if (rows.Values.Any(other => other != row && other["deletedAt"] is null && other["order"]!.GetValue<int>() == row["order"]!.GetValue<int>()))
                    return JsonResponse("{}", HttpStatusCode.Conflict);
                row["deletedAt"] = null;
                if (id == ids[1]) restoredFirst = true;
            }
            else if (request.Method == HttpMethod.Patch)
            {
                if (interrupt && restoredFirst && id == ids[1] && !interrupted)
                {
                    interrupted = true;
                    return JsonResponse("{}", HttpStatusCode.InternalServerError);
                }
                var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync())!;
                var presentation = body["presentation"]!;
                var order = presentation["order"]!.GetValue<int>();
                if (rows.Values.Any(other => other != row && other["deletedAt"] is null && other["order"]!.GetValue<int>() == order))
                    return JsonResponse("{}", HttpStatusCode.Conflict);
                row["order"] = order;
                row["isPublished"] = presentation["isPublished"]!.GetValue<bool>();
            }
            return JsonResponse(RegressionDetail(row).ToJsonString());
        }));
        var competition = Guid.NewGuid();
        if (interrupt)
        {
            try { await ApplyCompetitionChallengesAsync(client, competition, manifest, documents); throw new Exception("Expected an interrupted restore."); }
            catch (NoCtfApiException) when (interrupted) { }
        }
        await ApplyCompetitionChallengesAsync(client, competition, manifest, documents);
        Check(rows[ids[0]]["order"]!.GetValue<int>() == 10 && rows[ids[1]]["order"]!.GetValue<int>() == 30
            && rows[ids[2]]["order"]!.GetValue<int>() == 40 && rows.Values.All(row => row["deletedAt"] is null),
            "Restoration must handle occupied old slots, multiple tombstones and interrupted runs.");
    }

    private static JsonObject RegressionRow(Guid id, Guid template, int order, bool published, bool deleted) => new()
    { ["id"] = id, ["challengeId"] = template, ["order"] = order, ["isPublished"] = published,
        ["customTitle"] = null, ["deletedAt"] = deleted ? "2026-09-01T00:00:00Z" : null };

    private static JsonObject RegressionDetail(JsonObject row) => new()
    {
        ["challenge"] = row.DeepClone(),
        ["mode"] = "Ctf",
        ["competitionStatus"] = "Draft",
        ["rules"] = new JsonObject
        {
            ["mode"] = "Ctf",
            ["ctf"] = new JsonObject()
        }
    };

    private sealed class RegressionHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var response = await respond(request);
            response.RequestMessage = request;
            return response;
        }
    }
}
