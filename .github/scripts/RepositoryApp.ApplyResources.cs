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
    private static async Task ValidateAttachmentIdentitiesAsync(NoCtfClient client, ChallengeDocument document)
    {
        var existing = await GetManagedAttachmentsAsync(client, document.Id);
        foreach (var attachment in Sequence(document.Root, "attachments"))
        {
            var id = Guid.Parse(Scalar(attachment, "id"));
            var current = existing.SingleOrDefault(item => item["id"]!.GetValue<Guid>() == id);
            if (current is null) continue;
            var file = SafeChildPath(document.Directory, Scalar(attachment, "path"));
            var hash = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(file)));
            if (!NoCtfClient.AttachmentContentMatches(current, Path.GetFileName(file),
                Scalar(attachment, "contentType", "application/octet-stream"), new FileInfo(file).Length, hash))
                throw new InvalidOperationException($"Attachment {id} has different immutable content or metadata; allocate a new Attachment UUID.");
        }
    }

    private static async Task ApplyAttachmentsAsync(NoCtfClient client, ChallengeDocument document)
    {
        var existing = await GetManagedAttachmentsAsync(client, document.Id);
        var desired = Sequence(document.Root, "attachments")
            .ToDictionary(item => Guid.Parse(Scalar(item, "id")));
        foreach (var pair in desired)
        {
            var found = existing.SingleOrDefault(item => item["id"]!.GetValue<Guid>() == pair.Key);
            var path = SafeChildPath(document.Directory, Scalar(pair.Value, "path"));
            var hash = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(path)));
            if (found is not null && !NoCtfClient.AttachmentContentMatches(found, Path.GetFileName(path),
                Scalar(pair.Value, "contentType", "application/octet-stream"), new FileInfo(path).Length, hash))
                throw new InvalidOperationException($"Attachment {pair.Key} changed immutable content or metadata without changing ID.");
            if (found?["deletedAt"] is not null)
                await client.SendAsync(HttpMethod.Post, $"/api/v1/admin/challenges/{document.Id}/attachments/{pair.Key}/restore");
            if (found is null)
                await client.UploadAsync(
                    $"/api/v1/admin/challenges/{document.Id}/attachments",
                    pair.Key,
                    path,
                    Scalar(pair.Value, "contentType", "application/octet-stream"));
        }
        foreach (var item in existing.Where(item =>
                     item["deletedAt"] is null
                     && !desired.ContainsKey(item["id"]!.GetValue<Guid>())))
            await client.SendAsync(
                HttpMethod.Delete,
                $"/api/v1/admin/challenges/{document.Id}/attachments/{item["id"]!.GetValue<Guid>()}");
    }

    private static async Task ApplyFlagsAsync(NoCtfClient client, ChallengeDocument document)
    {
        var existing = await client.GetItemsAsync(
            $"/api/v1/admin/challenges/{document.Id}/flags?includeDeleted=true");
        var desired = Sequence(document.Root, "flags")
            .ToDictionary(item => Guid.Parse(Scalar(item, "id")));
        foreach (var pair in desired)
        {
            var found = existing.SingleOrDefault(item => item["id"]!.GetValue<Guid>() == pair.Key);
            if (found?["deletedAt"] is not null)
            {
                await client.SendAsync(HttpMethod.Post, $"/api/v1/admin/challenges/{document.Id}/flags/{pair.Key}/restore");
                found = await client.GetAsync(
                    $"/api/v1/admin/challenges/{document.Id}/flags/{pair.Key}");
            }
            var unchanged = found is not null
                && TextEquals(found, "flag", Scalar(pair.Value, "value"))
                && TextEquals(found, "matchKind", Scalar(pair.Value, "matchKind", "Exact"))
                && found["specificationKind"] is null
                && found["specificationId"] is null;
            if (unchanged)
                continue;
            var body = new
            {
                id = found is null ? pair.Key : (Guid?)null,
                flag = Scalar(pair.Value, "value"),
                matchKind = Scalar(pair.Value, "matchKind", "Exact")
            };
            var resourcePath = $"/api/v1/admin/challenges/{document.Id}/flags/{pair.Key}";
            if (found is null)
                await client.CreateAsync($"/api/v1/admin/challenges/{document.Id}/flags", body, resourcePath,
                    candidate => TextEquals(candidate, "flag", body.flag)
                        && TextEquals(candidate, "matchKind", body.matchKind)
                        && candidate["specificationKind"] is null && candidate["specificationId"] is null);
            else
                await client.SendAsync(HttpMethod.Put, resourcePath, body);
        }
        foreach (var item in existing.Where(item =>
                     item["deletedAt"] is null
                     && !desired.ContainsKey(item["id"]!.GetValue<Guid>())))
            await client.SendAsync(
                HttpMethod.Delete,
                $"/api/v1/admin/challenges/{document.Id}/flags/{item["id"]!.GetValue<Guid>()}");
    }

    private static async Task<List<JsonObject>> GetManagedAttachmentsAsync(
        NoCtfClient client,
        Guid challengeId)
    {
        var response = await client.GetAsync(
            $"/api/v1/admin/challenges/{challengeId}/attachments?includeDeleted=true")
            ?? throw new InvalidOperationException("NoCTF returned an empty attachment collection.");
        if (!TextEquals(response, "deliveryPolicy", "All"))
            throw new InvalidOperationException(
                $"Challenge {challengeId} uses RandomOnePerTeam attachments, which this GitOps manifest does not manage.");
        return response["items"]!.AsArray().Select(item => item!.AsObject()).ToList();
    }
}
