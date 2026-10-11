using System.Net.Http.Headers;
using System.Net.Http.Json;
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

    protected async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var token = await tokens.GetTokenAsync(ct);
        if (token is not null)
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await http.SendAsync(request, ct);
    }
}
