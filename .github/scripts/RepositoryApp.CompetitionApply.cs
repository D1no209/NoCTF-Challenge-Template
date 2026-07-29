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
    private static async Task ApplyCompetitionChallengesAsync(
        NoCtfClient client,
        Guid competitionId,
        YamlMappingNode competition,
        IReadOnlyDictionary<string, ChallengeDocument> documents)
    {
        var existing = await client.GetItemsAsync(
            $"/api/v1/admin/competitions/{competitionId}/challenges?includeDeleted=true");
        var desiredIds = new HashSet<Guid>();
        foreach (var item in Sequence(competition, "challenges"))
        {
            var id = Guid.Parse(Scalar(item, "id"));
            desiredIds.Add(id);
            var template = documents[NormalizePath(Scalar(item, "challenge"))];
            var current = existing.SingleOrDefault(value => value["id"]!.GetValue<Guid>() == id);
            if (current is null)
            {
                current = await client.SendAsync(
                    HttpMethod.Post,
                    $"/api/v1/admin/competitions/{competitionId}/challenges",
                    new
                    {
                        id,
                        challengeId = template.Id,
                        baseScore = long.Parse(Scalar(item, "baseScore")),
                        order = int.Parse(Scalar(item, "order"))
                    });
            }
            else if (current["deletedAt"] is not null)
            {
                await client.SendAsync(
                    HttpMethod.Post,
                    $"/api/v1/admin/competitions/{competitionId}/challenges/{id}/restore");
                current = await client.GetAsync(
                    $"/api/v1/admin/competitions/{competitionId}/challenges/{id}");
            }
            if (current!["challengeId"]!.GetValue<Guid>() != template.Id)
                throw new InvalidOperationException(
                    $"CompetitionChallenge {id} already belongs to another Challenge.");

            var rules = item.Children.TryGetValue(new YamlScalarNode("rules"), out var rulesNode)
                ? YamlToJson(rulesNode)!.ToJsonString()
                : """{"schemaVersion":1}""";
            var configuration = await client.GetAsync(
                $"/api/v1/admin/competitions/{competitionId}/challenges/{id}/configuration");
            var hintsMatch = await HintsMatchAsync(client, competitionId, id, item);
            var baseScore = long.Parse(Scalar(item, "baseScore"));
            var order = int.Parse(Scalar(item, "order"));
            var published = bool.Parse(Scalar(item, "published", "false"));
            var aggregateMatches = current["baseScore"]!.GetValue<long>() == baseScore
                                   && current["order"]!.GetValue<int>() == order
                                   && BoolEquals(current, "isPublished", published);
            if (aggregateMatches
                && JsonEquivalent(configuration!["json"]?.ToString(), rules)
                && hintsMatch)
            {
                ApplySummary.Add($"CompetitionChallenge `{id}` skipped (already converged).");
                continue;
            }

            if (!BoolEquals(current, "isPublished", false)
                || current["baseScore"]!.GetValue<long>() != baseScore
                || current["order"]!.GetValue<int>() != order)
                current = await UpdateCompetitionChallengeAsync(
                    client, competitionId, id, current, baseScore, order, false);

            if (!JsonEquivalent(configuration!["json"]?.ToString(), rules))
            {
                configuration = await client.SendAsync(
                    HttpMethod.Put,
                    $"/api/v1/admin/competitions/{competitionId}/challenges/{id}/configuration",
                    new
                    {
                        expectedRevision = configuration["revision"]!.GetValue<int>(),
                        json = rules
                    });
            }
            await ApplyHintsAsync(client, competitionId, id, item);
            var refreshed = await client.GetAsync(
                $"/api/v1/admin/competitions/{competitionId}/challenges/{id}")
                ?? throw new InvalidOperationException($"CompetitionChallenge {id} disappeared.");
            if (!BoolEquals(refreshed, "isPublished", published)
                || refreshed["baseScore"]!.GetValue<long>() != baseScore
                || refreshed["order"]!.GetValue<int>() != order)
                await UpdateCompetitionChallengeAsync(
                    client, competitionId, id, refreshed, baseScore, order, published);
            ApplySummary.Add($"CompetitionChallenge `{id}` converged.");
        }
        foreach (var item in existing.Where(value =>
                     value["deletedAt"] is null
                     && !desiredIds.Contains(value["id"]!.GetValue<Guid>())))
            await client.SendAsync(
                HttpMethod.Delete,
                $"/api/v1/admin/competitions/{competitionId}/challenges/{item["id"]!.GetValue<Guid>()}");
    }

    private static async Task ApplyHintsAsync(
        NoCtfClient client,
        Guid competitionId,
        Guid challengeId,
        YamlMappingNode challenge)
    {
        var existing = await client.GetItemsAsync(
            $"/api/v1/admin/competitions/{competitionId}/challenges/{challengeId}/hints?includeDeleted=true");
        var desired = Sequence(challenge, "hints")
            .ToDictionary(item => Guid.Parse(Scalar(item, "id")));
        foreach (var pair in desired)
        {
            var found = existing.SingleOrDefault(item => item["id"]!.GetValue<Guid>() == pair.Key);
            if (found?["deletedAt"] is not null)
            {
                await client.SendAsync(
                    HttpMethod.Post,
                    $"/api/v1/admin/competitions/{competitionId}/challenges/{challengeId}/hints/{pair.Key}/restore");
                found = await client.GetAsync(
                    $"/api/v1/admin/competitions/{competitionId}/challenges/{challengeId}/hints/{pair.Key}");
            }
            if (found is not null
                && TextEquals(found, "content", Scalar(pair.Value, "content"))
                && found["cost"]!.GetValue<long>() == long.Parse(Scalar(pair.Value, "cost"))
                && NullableInstantEquals(found["publishedAt"], NullScalar(pair.Value, "publishedAt")))
                continue;
            await client.SendAsync(
                found is null ? HttpMethod.Post : HttpMethod.Put,
                found is null
                    ? $"/api/v1/admin/competitions/{competitionId}/challenges/{challengeId}/hints"
                    : $"/api/v1/admin/competitions/{competitionId}/challenges/{challengeId}/hints/{pair.Key}",
                new
                {
                    id = found is null ? pair.Key : (Guid?)null,
                    content = Scalar(pair.Value, "content"),
                    cost = long.Parse(Scalar(pair.Value, "cost")),
                    publishedAt = NullScalar(pair.Value, "publishedAt")
                });
        }
        foreach (var item in existing.Where(value =>
                     value["deletedAt"] is null
                     && !desired.ContainsKey(value["id"]!.GetValue<Guid>())))
            await client.SendAsync(
                HttpMethod.Delete,
                $"/api/v1/admin/competitions/{competitionId}/challenges/{challengeId}/hints/{item["id"]!.GetValue<Guid>()}");
    }

    private static async Task<JsonObject> UpdateCompetitionChallengeAsync(
        NoCtfClient client,
        Guid competitionId,
        Guid id,
        JsonObject current,
        long baseScore,
        int order,
        bool published) =>
        await client.SendAsync(
            HttpMethod.Put,
            $"/api/v1/admin/competitions/{competitionId}/challenges/{id}",
            new
            {
                baseScore,
                order,
                isPublished = published,
                expectedRevision = current["revision"]!.GetValue<int>()
            });

    private static async Task<bool> HintsMatchAsync(
        NoCtfClient client,
        Guid competitionId,
        Guid challengeId,
        YamlMappingNode challenge)
    {
        var existing = await client.GetItemsAsync(
            $"/api/v1/admin/competitions/{competitionId}/challenges/{challengeId}/hints?includeDeleted=true");
        var desired = Sequence(challenge, "hints")
            .ToDictionary(item => Guid.Parse(Scalar(item, "id")));
        return existing.Where(item => item["deletedAt"] is null).All(item =>
                   desired.ContainsKey(item["id"]!.GetValue<Guid>()))
               && desired.All(pair =>
               {
                   var current = existing.SingleOrDefault(item =>
                       item["id"]!.GetValue<Guid>() == pair.Key);
                   return current is not null
                          && current["deletedAt"] is null
                          && TextEquals(current, "content", Scalar(pair.Value, "content"))
                          && current["cost"]!.GetValue<long>()
                          == long.Parse(Scalar(pair.Value, "cost"))
                          && NullableInstantEquals(
                              current["publishedAt"],
                              NullScalar(pair.Value, "publishedAt"));
               });
    }
}
