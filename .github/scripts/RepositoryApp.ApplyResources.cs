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
    private static async Task ApplyAttachmentsAsync(NoCtfClient client, ChallengeDocument document)
    {
        var existing = await client.GetItemsAsync(
            $"/api/v1/admin/challenges/{document.Id}/attachments?includeDeleted=true");
        var desired = Sequence(document.Root, "attachments")
            .ToDictionary(item => Guid.Parse(Scalar(item, "id")));
        foreach (var pair in desired)
        {
            var found = existing.SingleOrDefault(item => item["id"]!.GetValue<Guid>() == pair.Key);
            var path = SafeChildPath(document.Directory, Scalar(pair.Value, "path"));
            var hash = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(path)));
            if (found is not null && !string.Equals(found["sha256"]?.ToString(), hash, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"Attachment {pair.Key} changed content without changing ID.");
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
                && found["specificationKind"] is null
                && found["specificationId"] is null;
            if (unchanged)
                continue;
            var body = new
            {
                id = found is null ? pair.Key : (Guid?)null,
                flag = Scalar(pair.Value, "value"),
                specificationKind = (string?)null,
                specificationId = (Guid?)null
            };
            await client.SendAsync(
                found is null ? HttpMethod.Post : HttpMethod.Put,
                found is null
                    ? $"/api/v1/admin/challenges/{document.Id}/flags"
                    : $"/api/v1/admin/challenges/{document.Id}/flags/{pair.Key}",
                body);
        }
        foreach (var item in existing.Where(item =>
                     item["deletedAt"] is null
                     && !desired.ContainsKey(item["id"]!.GetValue<Guid>())))
            await client.SendAsync(
                HttpMethod.Delete,
                $"/api/v1/admin/challenges/{document.Id}/flags/{item["id"]!.GetValue<Guid>()}");
    }
}
