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
        await PrepareChallengeOrdersAsync(client, competitionId, competition, existing);
        var desiredIds = new HashSet<Guid>();
        foreach (var item in Sequence(competition, "challenges"))
        {
            var id = Guid.Parse(Scalar(item, "id"));
            desiredIds.Add(id);
            var template = documents[NormalizePath(Scalar(item, "challenge"))];
            var customTitle = NullScalar(item, "customTitle");
            var currentRow = existing.SingleOrDefault(value => value["id"]!.GetValue<Guid>() == id);
            if (currentRow is null)
            {
                currentRow = CompetitionChallengeResource(await client.CreateAsync(
                    $"/api/v1/admin/competitions/{competitionId}/challenges",
                    new
                    {
                        id,
                        challengeId = template.Id,
                        customTitle,
                        order = int.Parse(Scalar(item, "order"))
                    }, $"/api/v1/admin/competitions/{competitionId}/challenges/{id}", candidate =>
                    {
                        var challenge = CompetitionChallengeResource(candidate);
                        return challenge["challengeId"]!.GetValue<Guid>() == template.Id
                               && challenge["order"]!.GetValue<int>() == int.Parse(Scalar(item, "order"))
                               && challenge["customTitle"]?.GetValue<string>() == customTitle
                               && BoolEquals(challenge, "isPublished", false);
                    }));
            }
            if (currentRow!["challengeId"]!.GetValue<Guid>() != template.Id)
                throw new InvalidOperationException(
                    $"CompetitionChallenge {id} already belongs to another Challenge.");

            var rules = MaterializeRules(item, template.Mode);
            var detail = await client.GetAsync<AdminCompetitionChallengeResponse>(
                $"/api/v1/admin/competitions/{competitionId}/challenges/{id}");
            var current = detail!.Challenge;
            var currentRules = detail.Rules;
            var hintsMatch = await HintsMatchAsync(client, competitionId, id, item);
            var order = int.Parse(Scalar(item, "order"));
            var published = bool.Parse(Scalar(item, "published", "false"));
            var aggregateMatches = current.CustomTitle == customTitle
                                   && current.Order == order
                                   && current.IsPublished == published;
            if (aggregateMatches
                && JsonEquivalent(currentRules, rules)
                && hintsMatch)
            {
                ApplySummary.Add($"CompetitionChallenge `{id}` skipped (already converged).");
                continue;
            }

            var presentationChanged = current.CustomTitle != customTitle
                                      || current.Order != order;
            var rulesChanged = !JsonEquivalent(currentRules, rules);
            if (presentationChanged || rulesChanged)
                _ = await PatchCompetitionChallengeAsync(
                    client,
                    competitionId,
                    id,
                    presentationChanged,
                    customTitle,
                    order,
                    current.IsPublished,
                    rulesChanged ? rules : null);

            await ApplyHintsAsync(client, competitionId, id, item);
            var refreshedResponse = await client.GetAsync<AdminCompetitionChallengeResponse>(
                $"/api/v1/admin/competitions/{competitionId}/challenges/{id}")
                ?? throw new InvalidOperationException($"CompetitionChallenge {id} disappeared.");
            var refreshed = refreshedResponse.Challenge;
            if (refreshed.IsPublished != published
                || refreshed.CustomTitle != customTitle
                || refreshed.Order != order)
                await UpdateCompetitionChallengeAsync(
                    client, competitionId, id, customTitle, order, published);
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
            var body = new
            {
                id = found is null ? pair.Key : (Guid?)null,
                content = Scalar(pair.Value, "content"),
                cost = long.Parse(Scalar(pair.Value, "cost")),
                publishedAt = NullScalar(pair.Value, "publishedAt")
            };
            var resourcePath = $"/api/v1/admin/competitions/{competitionId}/challenges/{challengeId}/hints/{pair.Key}";
            if (found is null)
                await client.CreateAsync($"/api/v1/admin/competitions/{competitionId}/challenges/{challengeId}/hints",
                    body, resourcePath, candidate => TextEquals(candidate, "content", body.content)
                        && candidate["cost"]!.GetValue<long>() == body.cost
                        && NullableInstantEquals(candidate["publishedAt"], body.publishedAt));
            else
                await client.SendAsync(HttpMethod.Put, resourcePath, body);
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
        string? customTitle,
        int order,
        bool published) =>
        await PatchCompetitionChallengeAsync(
            client,
            competitionId,
            id,
            includePresentation: true,
            customTitle,
            order,
            published,
            rulesConfiguration: null);

    private static async Task<JsonObject> PatchCompetitionChallengeAsync(
        NoCtfClient client,
        Guid competitionId,
        Guid id,
        bool includePresentation,
        string? customTitle,
        int order,
        bool published,
        JsonObject? rulesConfiguration)
    {
        object? presentation = includePresentation
            ? new
            {
                customTitle,
                order,
                isPublished = published
            }
            : null;
        object? rules = rulesConfiguration is null
            ? null
            : new CompetitionChallengeRulesPatchRequest(rulesConfiguration);
        var response = await client.SendAsync(
            HttpMethod.Patch,
            $"/api/v1/admin/competitions/{competitionId}/challenges/{id}",
            new
            {
                presentation,
                rules
            });
        return CompetitionChallengeResource(response);
    }

    private static async Task PrepareChallengeOrdersAsync(NoCtfClient client, Guid competitionId,
        YamlMappingNode competition, List<JsonObject> existing)
    {
        var desired = Sequence(competition, "challenges").ToDictionary(
            item => Guid.Parse(Scalar(item, "id")), item => int.Parse(Scalar(item, "order")));
        var occupied = existing.Where(item => item["deletedAt"] is null).ToArray();
        var restoring = existing.Where(item => item["deletedAt"] is not null
            && desired.ContainsKey(item["id"]!.GetValue<Guid>())).ToArray();
        var restoreOrders = restoring.Select(item => item["order"]!.GetValue<int>()).ToHashSet();
        var reserved = occupied.Select(item => item["order"]!.GetValue<int>())
            .Concat(desired.Values).Concat(restoreOrders).ToHashSet();
        var nextOrder = 0;
        int AllocateOrder()
        {
            while (!reserved.Add(nextOrder))
            {
                if (nextOrder == int.MaxValue)
                    throw new InvalidOperationException("No free display order is available for reconciliation.");
                nextOrder++;
            }
            return nextOrder;
        }
        var moving = occupied.Where(item => restoreOrders.Contains(item["order"]!.GetValue<int>())
            || desired.Values.Contains(item["order"]!.GetValue<int>())
                && (!desired.TryGetValue(item["id"]!.GetValue<Guid>(), out var target)
                    || target != item["order"]!.GetValue<int>())).ToArray();
        foreach (var item in moving)
        {
            var id = item["id"]!.GetValue<Guid>();
            var refreshedResponse = await client.GetAsync($"/api/v1/admin/competitions/{competitionId}/challenges/{id}");
            var refreshed = CompetitionChallengeResource(refreshedResponse!);
            var updated = await UpdateCompetitionChallengeAsync(client, competitionId, id,
                refreshed["customTitle"]?.GetValue<string>(), AllocateOrder(),
                refreshed["isPublished"]!.GetValue<bool>());
            existing[existing.IndexOf(item)] = updated;
            ApplySummary.Add($"CompetitionChallenge `{id}` moved to a free temporary order for reconciliation.");
        }
        // Restore uses the tombstone's old order. Restore all tombstones before final ordering,
        // vacating each restored slot immediately because multiple tombstones may share it.
        foreach (var item in restoring)
        {
            var id = item["id"]!.GetValue<Guid>();
            var path = $"/api/v1/admin/competitions/{competitionId}/challenges/{id}";
            await client.SendAsync(HttpMethod.Post, path + "/restore");
            var restored = CompetitionChallengeResource((await client.GetAsync(path))!);
            var updated = await UpdateCompetitionChallengeAsync(client, competitionId, id,
                restored["customTitle"]?.GetValue<string>(), AllocateOrder(),
                restored["isPublished"]!.GetValue<bool>());
            existing[existing.IndexOf(item)] = updated;
            ApplySummary.Add($"CompetitionChallenge `{id}` restored and staged before final ordering.");
        }
    }

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

    private static JsonObject CompetitionChallengeResource(JsonObject response) =>
        response["challenge"]?.AsObject() ?? response;
}
