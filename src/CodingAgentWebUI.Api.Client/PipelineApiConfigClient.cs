using System.Net.Http.Json;
using CodingAgentWebUI.Pipeline;
using CodingAgentWebUI.Pipeline.Models;

namespace CodingAgentWebUI.Api.Client;

/// <summary>
/// <see cref="IPipelineApiConfigClient"/> backed by <see cref="HttpClient"/> registered
/// via <see cref="IHttpClientFactory"/>.
/// </summary>
internal sealed class PipelineApiConfigClient : IPipelineApiConfigClient
{
    private readonly HttpClient _http;

    public PipelineApiConfigClient(HttpClient http)
    {
        _http = http;
    }

    public async Task<PipelineConfiguration> GetPipelineConfigAsync(CancellationToken ct = default)
    {
        var result = await _http.GetFromJsonAsync<PipelineConfiguration>(
            "/api/config/pipeline",
            PipelineJsonOptions.Default,
            ct);
        return result!;
    }

    public async Task SavePipelineConfigAsync(PipelineConfiguration config, CancellationToken ct = default)
    {
        var response = await _http.PutAsJsonAsync("/api/config/pipeline", config, PipelineJsonOptions.Default, ct);
        response.EnsureSuccessStatusCode();
    }

    public async Task<IReadOnlyList<ProviderConfig>> GetProviderConfigsAsync(ProviderKind kind, CancellationToken ct = default)
    {
        var result = await _http.GetFromJsonAsync<List<ProviderConfig>>(
            $"/api/config/provider-configs?kind={kind}",
            PipelineJsonOptions.Default,
            ct);
        return result ?? [];
    }

    public async Task SaveProviderConfigAsync(ProviderConfig config, CancellationToken ct = default)
    {
        var response = await _http.PutAsJsonAsync("/api/config/provider-configs", config, PipelineJsonOptions.Default, ct);
        response.EnsureSuccessStatusCode();
    }

    public async Task DeleteProviderConfigAsync(string id, ProviderKind kind, CancellationToken ct = default)
    {
        var response = await _http.DeleteAsync($"/api/config/provider-configs/{Uri.EscapeDataString(id)}?kind={kind}", ct);
        response.EnsureSuccessStatusCode();
    }

    public async Task<IReadOnlyList<AgentProfile>> GetAgentProfilesAsync(CancellationToken ct = default)
    {
        var result = await _http.GetFromJsonAsync<List<AgentProfile>>(
            "/api/config/agent-profiles",
            PipelineJsonOptions.Default,
            ct);
        return result ?? [];
    }

    public async Task SaveAgentProfileAsync(AgentProfile profile, CancellationToken ct = default)
    {
        var response = await _http.PutAsJsonAsync("/api/config/agent-profiles", profile, PipelineJsonOptions.Default, ct);
        response.EnsureSuccessStatusCode();
    }

    public async Task<string?> GetKeyValueAsync(string key, CancellationToken ct = default)
    {
        var response = await _http.GetAsync($"/api/config/key-value/{Uri.EscapeDataString(key)}", ct);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            return null;
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<string>(cancellationToken: ct);
    }

    public async Task SetKeyValueAsync(string key, string value, CancellationToken ct = default)
    {
        var response = await _http.PutAsJsonAsync(
            $"/api/config/key-value/{Uri.EscapeDataString(key)}",
            value,
            PipelineJsonOptions.Default,
            ct);
        response.EnsureSuccessStatusCode();
    }
}
