using System.Net.Http.Json;
using System.Net.Http.Headers;
using CodingAgent.Pipeline;
using CodingAgent.Pipeline.Models;

namespace CodingAgent.Api.Client;

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
        return result ?? throw new InvalidOperationException(
            "GET /api/config/pipeline returned null — the API returned a non-JSON or empty response.");
    }

    public Task<IReadOnlyList<ProviderConfig>> GetProviderConfigsAsync(
        ProviderKind kind, CancellationToken ct = default)
        => FetchProviderConfigsAsync(kind, includeSecrets: false, ct);

    public Task<IReadOnlyList<ProviderConfig>> GetProviderConfigsWithSecretsAsync(
        ProviderKind kind, CancellationToken ct = default)
        => FetchProviderConfigsAsync(kind, includeSecrets: true, ct);

    private async Task<IReadOnlyList<ProviderConfig>> FetchProviderConfigsAsync(
        ProviderKind kind, bool includeSecrets, CancellationToken ct)
    {
        var result = await _http.GetFromJsonAsync<List<ProviderConfig>>(
            $"/api/config/provider-configs?kind={kind}&includeSecrets={includeSecrets}",
            PipelineJsonOptions.Default,
            ct);
        return result ?? [];
    }

    public async Task<IReadOnlyList<AgentProfile>> GetAgentProfilesAsync(CancellationToken ct = default)
    {
        var result = await _http.GetFromJsonAsync<List<AgentProfile>>(
            "/api/config/agent-profiles",
            PipelineJsonOptions.Default,
            ct);
        return result ?? [];
    }

    public async Task<IReadOnlyList<QualityGateConfiguration>> GetQualityGateConfigsAsync(CancellationToken ct = default)
    {
        var result = await _http.GetFromJsonAsync<List<QualityGateConfiguration>>(
            "/api/config/quality-gate-configs",
            PipelineJsonOptions.Default,
            ct);
        return result ?? [];
    }

    public async Task<IReadOnlyList<ReviewerConfiguration>> GetReviewerConfigsAsync(CancellationToken ct = default)
    {
        var result = await _http.GetFromJsonAsync<List<ReviewerConfiguration>>(
            "/api/config/reviewer-configs",
            PipelineJsonOptions.Default,
            ct);
        return result ?? [];
    }

    public async Task<IReadOnlyList<PipelineProject>> GetProjectsAsync(CancellationToken ct = default)
    {
        var result = await _http.GetFromJsonAsync<List<PipelineProject>>(
            "/api/config/projects",
            PipelineJsonOptions.Default,
            ct);
        return result ?? [];
    }

    public async Task<IReadOnlyList<PipelineJobTemplate>> GetAllTemplatesAsync(CancellationToken ct = default)
    {
        var result = await _http.GetFromJsonAsync<List<PipelineJobTemplate>>(
            "/api/config/templates",
            PipelineJsonOptions.Default,
            ct);
        return result ?? [];
    }
}
