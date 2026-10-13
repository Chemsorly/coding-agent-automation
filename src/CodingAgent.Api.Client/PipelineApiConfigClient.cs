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

    public async Task SavePipelineConfigAsync(PipelineConfiguration config, CancellationToken ct = default)
    {
        var response = await _http.PutAsJsonAsync("/api/config/pipeline", config, PipelineJsonOptions.Default, ct);
        // A 400 names the settings outside their range; the message is shown as is on the settings pages.
        if (response.StatusCode == System.Net.HttpStatusCode.BadRequest)
            await EnsureAcceptedAsync(response, ct);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(ct);
            throw new InvalidOperationException(
                $"SavePipelineConfig failed ({(int)response.StatusCode}): {body}");
        }
    }

    public async Task UpdatePipelineConfigAsync(Func<PipelineConfiguration, PipelineConfiguration> transform, CancellationToken ct = default)
    {
        var current = await GetPipelineConfigAsync(ct);
        var updated = transform(current);
        await SavePipelineConfigAsync(updated, ct);
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

    public async Task<PipelineProject?> GetProjectByIdAsync(string id, CancellationToken ct = default)
    {
        var response = await _http.GetAsync($"/api/config/projects/{Uri.EscapeDataString(id)}", ct);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            return null;
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<PipelineProject>(PipelineJsonOptions.Default, ct);
    }

    public async Task<IReadOnlyList<PipelineJobTemplate>> GetAllTemplatesAsync(CancellationToken ct = default)
    {
        var result = await _http.GetFromJsonAsync<List<PipelineJobTemplate>>(
            "/api/config/templates",
            PipelineJsonOptions.Default,
            ct);
        return result ?? [];
    }

    public async Task<IReadOnlyList<PipelineJobTemplate>> GetTemplatesForProjectAsync(string projectId, CancellationToken ct = default)
    {
        var result = await _http.GetFromJsonAsync<List<PipelineJobTemplate>>(
            $"/api/config/projects/{Uri.EscapeDataString(projectId)}/templates",
            PipelineJsonOptions.Default,
            ct);
        return result ?? [];
    }

    public async Task SaveTemplateAsync(string projectId, PipelineJobTemplate template, CancellationToken ct = default)
    {
        var response = await _http.PutAsJsonAsync(
            $"/api/config/projects/{Uri.EscapeDataString(projectId)}/templates",
            template,
            PipelineJsonOptions.Default,
            ct);
        await EnsureAcceptedAsync(response, ct);
    }

    public async Task DeleteTemplateAsync(string projectId, string templateId, CancellationToken ct = default)
    {
        var response = await _http.DeleteAsync(
            $"/api/config/projects/{Uri.EscapeDataString(projectId)}/templates/{Uri.EscapeDataString(templateId)}",
            ct);
        response.EnsureSuccessStatusCode();
    }

    public async Task MoveTemplateAsync(ProjectId sourceProjectId, ProjectId targetProjectId, string templateId, CancellationToken ct = default)
    {
        var response = await _http.PostAsJsonAsync(
            "/api/config/templates/move",
            new { SourceProjectId = sourceProjectId.Value, TargetProjectId = targetProjectId.Value, TemplateId = templateId },
            PipelineJsonOptions.Default,
            ct);
        await EnsureAcceptedAsync(response, ct);
    }

    /// <summary>
    /// Like <see cref="HttpResponseMessage.EnsureSuccessStatusCode"/>, but when the API refuses the request
    /// (400 or 404), the exception message is the API's reason, for example a template binding conflict,
    /// so the UI can show it.
    /// </summary>
    private static async Task EnsureAcceptedAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.StatusCode is System.Net.HttpStatusCode.BadRequest or System.Net.HttpStatusCode.NotFound)
        {
            var body = await response.Content.ReadAsStringAsync(ct);
            var reason = body.StartsWith('"') ? System.Text.Json.JsonSerializer.Deserialize<string>(body) : body;
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(reason) ? $"The API returned {(int)response.StatusCode}." : reason);
        }

        response.EnsureSuccessStatusCode();
    }
}
