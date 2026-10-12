using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace CodingAgent.Web.Ui;

/// <summary>
/// Base of the typed clients for the Pipeline API's UI surface (spec 051 D6). Every request gets
/// the current user's token from <see cref="IUiAccessTokenSource"/>. The token is set on the
/// request, never on the client: the handlers behind <see cref="HttpClient"/> are pooled and shared
/// between users. JSON goes through source-generated metadata (<see cref="UiJsonContext"/>), which
/// stays intact when a WebAssembly build trims the app.
/// </summary>
public abstract class UiApiClient(HttpClient http, IUiAccessTokenSource tokens)
{
    protected async Task<T> GetAsync<T>(string path, JsonTypeInfo<T> type, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        using var response = await SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync(type, ct)
            ?? throw new InvalidOperationException($"GET {path} returned no content.");
    }

    /// <summary>GET that answers null for a 404.</summary>
    protected async Task<T?> FindAsync<T>(string path, JsonTypeInfo<T> type, CancellationToken ct) where T : class
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        using var response = await SendAsync(request, ct);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return null;
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync(type, ct);
    }

    /// <summary>Sends <paramref name="body"/> as JSON; a refusal (400, 404, 409, 502) throws with the API's reason.</summary>
    protected async Task SendJsonAsync<T>(HttpMethod method, string path, T body, JsonTypeInfo<T> type, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, path) { Content = JsonContent.Create(body, type) };
        using var response = await SendAsync(request, ct);
        await EnsureAcceptedAsync(response, ct);
    }

    /// <summary>Sends a request and reads its JSON result; a refusal (400, 404, 409, 502) throws with the API's reason.</summary>
    protected async Task<T> SendForResultAsync<T>(HttpMethod method, string path, HttpContent? content, JsonTypeInfo<T> type, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, path) { Content = content };
        using var response = await SendAsync(request, ct);
        await EnsureAcceptedAsync(response, ct);
        return (await response.Content.ReadFromJsonAsync(type, ct))!;
    }

    /// <summary>Sends a request without a body; a refusal (400, 404, 409, 502) throws with the API's reason.</summary>
    protected async Task SendAsync(HttpMethod method, string path, CancellationToken ct, HttpContent? content = null)
    {
        using var request = new HttpRequestMessage(method, path) { Content = content };
        using var response = await SendAsync(request, ct);
        await EnsureAcceptedAsync(response, ct);
    }

    protected async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var token = await tokens.GetTokenAsync(ct);
        if (token is not null)
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await http.SendAsync(request, ct);
    }

    /// <summary>
    /// Like <see cref="HttpResponseMessage.EnsureSuccessStatusCode"/>, but a refusal (400, 404, 409 or 502)
    /// throws <see cref="InvalidOperationException"/> with the API's reason, so the page can show it.
    /// </summary>
    private static async Task EnsureAcceptedAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.NotFound or HttpStatusCode.Conflict or HttpStatusCode.BadGateway)
            throw new InvalidOperationException(await ReadReasonAsync(response, ct));

        response.EnsureSuccessStatusCode();
    }

    /// <summary>The reason the API gave for a failed request: its JSON string body, or the raw body.</summary>
    protected static async Task<string> ReadReasonAsync(HttpResponseMessage response, CancellationToken ct)
    {
        var body = await response.Content.ReadAsStringAsync(ct);
        var reason = body.StartsWith('"') ? JsonSerializer.Deserialize(body, UiJsonContext.Default.String) : body;
        return string.IsNullOrWhiteSpace(reason) ? $"The API returned {(int)response.StatusCode}." : reason;
    }
}
