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

    public NoCtfClient(string baseUrl, string token)
    {
        client = new HttpClient { BaseAddress = new Uri(baseUrl.TrimEnd('/') + "/") };
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

    public async Task<List<JsonObject>> GetItemsAsync(string path)
    {
        var result = await GetAsync(path);
        return result!["items"]!.AsArray().Select(item => item!.AsObject()).ToList();
    }

    public async Task<JsonObject> SendAsync(HttpMethod method, string path, object? body = null)
    {
        using var response = await SendWithRetryAsync(() =>
        {
            var request = new HttpRequestMessage(method, path);
            if (body is not null)
                request.Content = JsonContent.Create(body);
            return request;
        });
        await EnsureSuccessAsync(response);
        if (response.StatusCode == HttpStatusCode.NoContent)
            return [];
        return await response.Content.ReadFromJsonAsync<JsonObject>() ?? [];
    }

    public async Task UploadAsync(string path, Guid id, string file, string contentType)
    {
        using var response = await SendWithRetryAsync(() =>
        {
            var request = new HttpRequestMessage(HttpMethod.Post, path);
            var form = new MultipartFormDataContent();
            form.Add(new StringContent(id.ToString()), "Id");
            var content = new StreamContent(File.OpenRead(file));
            content.Headers.ContentType = new MediaTypeHeaderValue(contentType);
            form.Add(content, "File", Path.GetFileName(file));
            request.Content = form;
            return request;
        });
        await EnsureSuccessAsync(response);
    }

    private async Task<HttpResponseMessage> SendWithRetryAsync(
        Func<HttpRequestMessage> requestFactory)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                var response = await client.SendAsync(requestFactory());
                if (attempt < 3
                    && (response.StatusCode == HttpStatusCode.TooManyRequests
                        || (int)response.StatusCode >= 500))
                {
                    response.Dispose();
                    await Task.Delay(TimeSpan.FromSeconds(Math.Pow(2, attempt)));
                    continue;
                }
                return response;
            }
            catch (HttpRequestException) when (attempt < 3)
            {
                await Task.Delay(TimeSpan.FromSeconds(Math.Pow(2, attempt)));
            }
        }
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response)
    {
        if (response.IsSuccessStatusCode)
            return;
        var body = await response.Content.ReadAsStringAsync();
        throw new InvalidOperationException(
            $"NoCTF API returned {(int)response.StatusCode} {response.StatusCode}: {body}");
    }

    public void Dispose() => client.Dispose();
}
