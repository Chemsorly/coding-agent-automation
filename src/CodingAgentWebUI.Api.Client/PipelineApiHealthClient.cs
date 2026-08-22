namespace CodingAgentWebUI.Api.Client;

/// <summary>
/// <see cref="IPipelineApiHealthClient"/> backed by <see cref="HttpClient"/> registered
/// via <see cref="IHttpClientFactory"/>.
/// </summary>
internal sealed class PipelineApiHealthClient : IPipelineApiHealthClient
{
    private readonly HttpClient _http;

    public PipelineApiHealthClient(HttpClient http)
    {
        _http = http;
    }

    public async Task<bool> IsHealthyAsync(CancellationToken ct = default)
    {
        try
        {
            var response = await _http.GetAsync("/healthz", ct);
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    public async Task<bool> IsReadyAsync(CancellationToken ct = default)
    {
        try
        {
            var response = await _http.GetAsync("/readyz", ct);
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }
}
