using System.Net.Http.Json;
using System.Net.Http.Headers;
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

    public async Task DeleteAgentProfileAsync(string id, CancellationToken ct = default)
    {
        var response = await _http.DeleteAsync($"/api/config/agent-profiles/{Uri.EscapeDataString(id)}", ct);
        response.EnsureSuccessStatusCode();
    }

    public async Task<IReadOnlyList<QualityGateConfiguration>> GetQualityGateConfigsAsync(CancellationToken ct = default)
    {
        var result = await _http.GetFromJsonAsync<List<QualityGateConfiguration>>(
            "/api/config/quality-gate-configs",
            PipelineJsonOptions.Default,
            ct);
        return result ?? [];
    }

    public async Task SaveQualityGateConfigAsync(QualityGateConfiguration config, CancellationToken ct = default)
    {
        var response = await _http.PutAsJsonAsync("/api/config/quality-gate-configs", config, PipelineJsonOptions.Default, ct);
        response.EnsureSuccessStatusCode();
    }

    public async Task DeleteQualityGateConfigAsync(string id, CancellationToken ct = default)
    {
        var response = await _http.DeleteAsync($"/api/config/quality-gate-configs/{Uri.EscapeDataString(id)}", ct);
        response.EnsureSuccessStatusCode();
    }

    public async Task<IReadOnlyList<ReviewerConfiguration>> GetReviewerConfigsAsync(CancellationToken ct = default)
    {
        var result = await _http.GetFromJsonAsync<List<ReviewerConfiguration>>(
            "/api/config/reviewer-configs",
            PipelineJsonOptions.Default,
            ct);
        return result ?? [];
    }

    public async Task SaveReviewerConfigAsync(ReviewerConfiguration config, CancellationToken ct = default)
    {
        var response = await _http.PutAsJsonAsync("/api/config/reviewer-configs", config, PipelineJsonOptions.Default, ct);
        response.EnsureSuccessStatusCode();
    }

    public async Task DeleteReviewerConfigAsync(string id, CancellationToken ct = default)
    {
        var response = await _http.DeleteAsync($"/api/config/reviewer-configs/{Uri.EscapeDataString(id)}", ct);
        response.EnsureSuccessStatusCode();
    }

    public async Task ResetReviewerConfigsToDefaultAsync(CancellationToken ct = default)
    {
        var response = await _http.PostAsync("/api/config/reviewer-configs/reset-to-defaults", null, ct);
        response.EnsureSuccessStatusCode();
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

    public async Task SaveProjectAsync(PipelineProject project, CancellationToken ct = default)
    {
        var response = await _http.PutAsJsonAsync("/api/config/projects", project, PipelineJsonOptions.Default, ct);
        response.EnsureSuccessStatusCode();
    }

    public async Task DeleteProjectAsync(string id, CancellationToken ct = default)
    {
        var response = await _http.DeleteAsync($"/api/config/projects/{Uri.EscapeDataString(id)}", ct);
        response.EnsureSuccessStatusCode();
    }

    public async Task<bool> HasEnabledTemplatesAsync(CancellationToken ct = default)
    {
        var response = await _http.GetAsync("/api/config/projects/has-enabled-templates", ct);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<bool>(cancellationToken: ct);
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
        response.EnsureSuccessStatusCode();
    }

    public async Task DeleteTemplateAsync(string projectId, string templateId, CancellationToken ct = default)
    {
        var response = await _http.DeleteAsync(
            $"/api/config/projects/{Uri.EscapeDataString(projectId)}/templates/{Uri.EscapeDataString(templateId)}",
            ct);
        response.EnsureSuccessStatusCode();
    }

    public async Task MoveTemplateAsync(string sourceProjectId, string targetProjectId, string templateId, CancellationToken ct = default)
    {
        var response = await _http.PostAsJsonAsync(
            "/api/config/templates/move",
            new { SourceProjectId = sourceProjectId, TargetProjectId = targetProjectId, TemplateId = templateId },
            PipelineJsonOptions.Default,
            ct);
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

    public async Task DeleteKeyValueAsync(string key, CancellationToken ct = default)
    {
        var response = await _http.DeleteAsync($"/api/config/key-value/{Uri.EscapeDataString(key)}", ct);
        response.EnsureSuccessStatusCode();
    }

    /// <inheritdoc/>
    public async Task<byte[]> ExportConfigAsync(CancellationToken ct = default)
    {
        var response = await _http.GetAsync("/api/config/export", ct);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsByteArrayAsync(ct);
    }

    /// <inheritdoc/>
    public async Task ImportConfigAsync(Stream jsonStream, string fileName, CancellationToken ct = default)
    {
        using var content = new MultipartFormDataContent();
        var fileContent = new StreamContent(jsonStream);
        fileContent.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        content.Add(fileContent, "file", fileName);
        var response = await _http.PostAsync("/api/config/import", content, ct);
        response.EnsureSuccessStatusCode();
    }

    /// <inheritdoc/>
    /// Delegates to GET /api/config/models, which dispatches a one-shot K8s Job on the API
    /// to query available models from the Kiro CLI (Spec 045 Req 7a.2 Option A).
    public async Task<(IReadOnlyList<AgentModelInfo> Models, string? Error)> GetModelsAsync(CancellationToken ct = default)
    {
        var response = await _http.GetAsync("/api/config/models", ct);
        if (!response.IsSuccessStatusCode)
        {
            var problem = await response.Content.ReadAsStringAsync(ct);
            return ([], problem);
        }
        var models = await response.Content.ReadFromJsonAsync<List<AgentModelInfo>>(PipelineJsonOptions.Default, ct);
        return (models ?? [], null);
    }
}
