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
internal sealed class NoCtfClient : IDisposable
{
    private readonly HttpClient client;

    public NoCtfClient(string baseUrl, string token, HttpMessageHandler? handler = null)
    {
        if (!Uri.TryCreate(baseUrl.TrimEnd('/') + "/", UriKind.Absolute, out var uri)
            || uri.Scheme is not ("http" or "https"))
            throw new InvalidOperationException(
                "NOCTF_API_URL must be an absolute HTTP or HTTPS URL.");
        if (!string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
            throw new InvalidOperationException("NOCTF_API_URL cannot contain credentials, a query or a fragment.");
        client = new HttpClient(handler ?? new HttpClientHandler { AllowAutoRedirect = false }) { BaseAddress = uri };
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
    }

    public async Task<JsonObject?> GetAsync(string path, bool allowNotFound = false)
    {
        using var response = await SendWithRetryAsync(() => new HttpRequestMessage(HttpMethod.Get, path));
        if (allowNotFound && response.StatusCode == HttpStatusCode.NotFound)
            return null;
        await EnsureSuccessAsync(response);
        return await response.Content.ReadFromJsonAsync<JsonObject>()
            ?? throw new InvalidOperationException($"{path} returned an empty response.");
    }

    public async Task<T?> GetAsync<T>(string path, bool allowNotFound = false)
        where T : class
    {
        using var response = await SendWithRetryAsync(() => new HttpRequestMessage(HttpMethod.Get, path));
        if (allowNotFound && response.StatusCode == HttpStatusCode.NotFound)
            return null;
        await EnsureSuccessAsync(response);
        return await response.Content.ReadFromJsonAsync<T>()
            ?? throw new InvalidOperationException($"{path} returned an empty response.");
    }

    public async Task<List<JsonObject>> GetItemsAsync(string path)
    {
        var result = await GetAsync(path);
        return result!["items"]!.AsArray().Select(item => item!.AsObject()).ToList();
    }

    // A committed create can lose its HTTP response. Re-read its stable ID before deciding
    // whether a retry succeeded; never treat a mismatching 409 as success.
    public async Task<JsonObject> CreateAsync(string path, object body, string resourcePath,
        Func<JsonObject, bool> matches)
    {
        try { return await SendAsync(HttpMethod.Post, path, body); }
        catch (NoCtfApiException error) when (error.StatusCode == HttpStatusCode.Conflict || (int)error.StatusCode >= 500)
        {
            var current = await GetAsync(resourcePath, allowNotFound: true);
            if (current is not null && matches(current)) return current;
            throw;
        }
        catch (Exception error) when (error is HttpRequestException or TaskCanceledException)
        {
            var current = await GetAsync(resourcePath, allowNotFound: true);
            if (current is not null && matches(current)) return current;
            throw;
        }
    }

    public async Task<T> CreateAsync<T>(string path, object body, string resourcePath,
        Func<T, bool> matches)
        where T : class
    {
        try { return await SendAsync<T>(HttpMethod.Post, path, body); }
        catch (NoCtfApiException error) when (error.StatusCode == HttpStatusCode.Conflict || (int)error.StatusCode >= 500)
        {
            var current = await GetAsync<T>(resourcePath, allowNotFound: true);
            if (current is not null && matches(current)) return current;
            throw;
        }
        catch (Exception error) when (error is HttpRequestException or TaskCanceledException)
        {
            var current = await GetAsync<T>(resourcePath, allowNotFound: true);
            if (current is not null && matches(current)) return current;
            throw;
        }
    }

    public async Task<JsonObject> SendAsync(HttpMethod method, string path, object? body = null)
    {
        using var response = await SendWithRetryAsync(() =>
        {
            var request = new HttpRequestMessage(method, path);
            if (body is not null)
                request.Content = JsonContent.Create(body);
            else if (method == HttpMethod.Post || method == HttpMethod.Put)
                request.Content = JsonContent.Create(new { });
            return request;
        });
        await EnsureSuccessAsync(response);
        if (response.StatusCode == HttpStatusCode.NoContent)
            return [];
        return await response.Content.ReadFromJsonAsync<JsonObject>() ?? [];
    }

    public async Task<T> SendAsync<T>(HttpMethod method, string path, object? body = null)
        where T : class
    {
        using var response = await SendWithRetryAsync(() =>
        {
            var request = new HttpRequestMessage(method, path);
            if (body is not null)
                request.Content = JsonContent.Create(body);
            else if (method == HttpMethod.Post || method == HttpMethod.Put)
                request.Content = JsonContent.Create(new { });
            return request;
        });
        await EnsureSuccessAsync(response);
        return await response.Content.ReadFromJsonAsync<T>()
            ?? throw new InvalidOperationException($"{path} returned an empty response.");
    }

    public async Task UploadAsync(string path, Guid id, string file, string contentType)
    {
        try
        {
        using var response = await SendWithRetryAsync(() =>
        {
            var request = new HttpRequestMessage(HttpMethod.Post, path);
            var form = new MultipartFormDataContent();
            form.Add(new StringContent("All"), "DeliveryPolicy");
            form.Add(new StringContent(id.ToString()), "AttachmentIds");
            var content = new StreamContent(File.OpenRead(file));
            content.Headers.ContentType = new MediaTypeHeaderValue(contentType);
            form.Add(content, "Files", Path.GetFileName(file));
            request.Content = form;
            return request;
        });
        await EnsureSuccessAsync(response);
        }
        catch (Exception error) when (error is HttpRequestException or TaskCanceledException
            || error is NoCtfApiException api && (api.StatusCode == HttpStatusCode.Conflict || (int)api.StatusCode >= 500))
        {
            var current = (await GetItemsAsync(path + "?includeDeleted=true"))
                .SingleOrDefault(item => item["id"]!.GetValue<Guid>() == id);
            var hash = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(file)));
            if (current is not null && current["deletedAt"] is null
                && AttachmentContentMatches(current, Path.GetFileName(file), contentType, new FileInfo(file).Length, hash))
                return;
            throw;
        }
    }

    public static bool AttachmentContentMatches(JsonObject attachment, string fileName, string contentType, long byteLength, string sha256) =>
        string.Equals(attachment["sha256"]?.ToString(), sha256, StringComparison.OrdinalIgnoreCase)
        && attachment["fileName"]?.GetValue<string>() == fileName
        && attachment["contentType"]?.GetValue<string>() == new MediaTypeHeaderValue(contentType).ToString()
        && attachment["byteLength"]?.GetValue<long>() == byteLength;

    private async Task<HttpResponseMessage> SendWithRetryAsync(
        Func<HttpRequestMessage> requestFactory)
    {
        for (var attempt = 0; ; attempt++)
        {
            var safeToRetry = false;
            try
            {
                using var request = requestFactory();
                if (request.RequestUri is null || request.RequestUri.IsAbsoluteUri
                    || !request.RequestUri.OriginalString.StartsWith("/api/v1/", StringComparison.Ordinal))
                    throw new InvalidOperationException("Only relative NoCTF API routes may receive the Bot token.");
                safeToRetry = request.Method == HttpMethod.Get || request.Method == HttpMethod.Head;
                var response = await client.SendAsync(request);
                if (safeToRetry && attempt < 3
                    && (response.StatusCode == HttpStatusCode.TooManyRequests
                        || (int)response.StatusCode >= 500))
                {
                    response.Dispose();
                    await Task.Delay(TimeSpan.FromSeconds(Math.Pow(2, attempt)));
                    continue;
                }
                return response;
            }
            catch (Exception error) when (safeToRetry && attempt < 3 && error is HttpRequestException or TaskCanceledException)
            {
                await Task.Delay(TimeSpan.FromSeconds(Math.Pow(2, attempt)));
            }
        }
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response)
    {
        if (response.IsSuccessStatusCode)
            return;
        string? code = null;
        try
        {
            var body = await response.Content.ReadFromJsonAsync<JsonObject>();
            var candidate = body?["code"]?.ToString();
            if (candidate is not null && Regex.IsMatch(candidate, "^[A-Za-z][A-Za-z0-9_]{0,95}$"))
                code = candidate;
        }
        catch (Exception error) when (error is JsonException or NotSupportedException) { }
        // Never copy arbitrary response bodies into CI logs: they can contain Flags,
        // credentials or reflected manifest contents.
        throw new NoCtfApiException(response.StatusCode, code,
            response.RequestMessage?.Method.Method ?? "HTTP",
            response.RequestMessage?.RequestUri?.AbsolutePath ?? "/api/v1");
    }

    public void Dispose() => client.Dispose();
}

internal sealed class NoCtfApiException(HttpStatusCode statusCode, string? code, string method, string path)
    : Exception($"NoCTF {method} {path}: HTTP {(int)statusCode} {statusCode} ({code ?? "RequestRejected"}). Check resource permissions, lifecycle and validation preflight; no response secrets were logged.")
{
    public HttpStatusCode StatusCode { get; } = statusCode;
    public string? Code { get; } = code;
}
