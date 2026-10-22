using System.Net.Http.Headers;
using CodingAgent.Contracts.Ui;
using CodingAgent.Pipeline.Models;

namespace CodingAgent.Web.Ui;

/// <summary>
/// Settings, admin only (spec 051 2.1b, <c>/api/ui/settings</c>). Method names match the former
/// master-key client so the Settings sections did not change. Project secrets and MCP server env
/// values and headers come back as "****"; saving "****" keeps the stored value.
/// </summary>
public interface IUiSettingsClient
{
    Task<PipelineConfiguration> GetPipelineConfigAsync(CancellationToken ct = default);

    /// <summary>Reads the configuration, applies <paramref name="transform"/> and saves the result.</summary>
    Task UpdatePipelineConfigAsync(Func<PipelineConfiguration, PipelineConfiguration> transform, CancellationToken ct = default);

    /// <summary>The Agent Profiles page: the profiles (MCP server secrets masked) and the job templates they can run on.</summary>
    Task<UiAgentProfilesPage> GetAgentProfilesAsync(CancellationToken ct = default);

    /// <summary>A profile needs tool settings and a job template with a tool; a disable or delete is refused while routes name it.</summary>
    Task SaveAgentProfileAsync(AgentProfile profile, CancellationToken ct = default);
    Task DeleteAgentProfileAsync(string id, CancellationToken ct = default);

    /// <summary>The Routing page: the global routing table, the profiles a route can name and their job templates.</summary>
    Task<UiAgentRoutingPage> GetAgentRoutingAsync(CancellationToken ct = default);

    /// <summary>Replaces the global routing table. An invalid table is refused with one line per invalid row.</summary>
    Task SaveAgentRoutingAsync(AgentRouting routing, CancellationToken ct = default);

    Task<IReadOnlyList<QualityGateConfiguration>> GetQualityGateConfigsAsync(CancellationToken ct = default);
    Task SaveQualityGateConfigAsync(QualityGateConfiguration config, CancellationToken ct = default);
    Task DeleteQualityGateConfigAsync(string id, CancellationToken ct = default);

    Task<IReadOnlyList<ReviewerConfiguration>> GetReviewerConfigsAsync(CancellationToken ct = default);
    Task SaveReviewerConfigAsync(ReviewerConfiguration config, CancellationToken ct = default);
    Task DeleteReviewerConfigAsync(string id, CancellationToken ct = default);
    Task ResetReviewerConfigsToDefaultAsync(CancellationToken ct = default);

    Task<IReadOnlyList<PipelineProject>> GetProjectsAsync(CancellationToken ct = default);
    Task<PipelineProject?> GetProjectByIdAsync(string id, CancellationToken ct = default);
    Task SaveProjectAsync(PipelineProject project, CancellationToken ct = default);
    Task DeleteProjectAsync(string id, CancellationToken ct = default);

    Task<IReadOnlyList<PipelineJobTemplate>> GetAllTemplatesAsync(CancellationToken ct = default);
    Task MoveTemplateAsync(ProjectId sourceProjectId, ProjectId targetProjectId, string templateId, CancellationToken ct = default);

    /// <summary>Adds or replaces a template of the project. A template that clashes with another (same repository or tracker) is refused with the reason.</summary>
    Task SaveTemplateAsync(ProjectId projectId, PipelineJobTemplate template, CancellationToken ct = default);

    Task DeleteTemplateAsync(ProjectId projectId, TemplateId templateId, CancellationToken ct = default);

    /// <summary>The full configuration bundle, unredacted (spec 051 Q1): it contains live credentials.</summary>
    Task<byte[]> ExportConfigAsync(CancellationToken ct = default);
    Task ImportConfigAsync(Stream jsonStream, string fileName, CancellationToken ct = default);

    Task<UiFirstRunState> GetFirstRunStateAsync(CancellationToken ct = default);
    Task DismissFirstRunBannerAsync(CancellationToken ct = default);
}

public sealed class UiSettingsClient(HttpClient http, IUiAccessTokenSource tokens) : UiApiClient(http, tokens), IUiSettingsClient
{
    private const string Settings = "/api/ui/settings";

    private static string Id(string id) => Uri.EscapeDataString(id);

    public Task<PipelineConfiguration> GetPipelineConfigAsync(CancellationToken ct = default) =>
        GetAsync($"{Settings}/pipeline", UiJsonContext.Default.PipelineConfiguration, ct);

    public async Task UpdatePipelineConfigAsync(Func<PipelineConfiguration, PipelineConfiguration> transform, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(transform);
        var updated = transform(await GetPipelineConfigAsync(ct));
        await SendJsonAsync(HttpMethod.Put, $"{Settings}/pipeline", updated, UiJsonContext.Default.PipelineConfiguration, ct);
    }

    public Task<UiAgentProfilesPage> GetAgentProfilesAsync(CancellationToken ct = default) =>
        GetAsync($"{Settings}/agent-profiles", UiJsonContext.Default.UiAgentProfilesPage, ct);

    public Task SaveAgentProfileAsync(AgentProfile profile, CancellationToken ct = default) =>
        SendJsonAsync(HttpMethod.Put, $"{Settings}/agent-profiles", profile, UiJsonContext.Default.AgentProfile, ct);

    public Task DeleteAgentProfileAsync(string id, CancellationToken ct = default) =>
        SendAsync(HttpMethod.Delete, $"{Settings}/agent-profiles/{Id(id)}", ct);

    public Task<UiAgentRoutingPage> GetAgentRoutingAsync(CancellationToken ct = default) =>
        GetAsync($"{Settings}/agent-routing", UiJsonContext.Default.UiAgentRoutingPage, ct);

    public Task SaveAgentRoutingAsync(AgentRouting routing, CancellationToken ct = default) =>
        SendJsonAsync(HttpMethod.Put, $"{Settings}/agent-routing", routing, UiJsonContext.Default.AgentRouting, ct);

    public Task<IReadOnlyList<QualityGateConfiguration>> GetQualityGateConfigsAsync(CancellationToken ct = default) =>
        GetAsync($"{Settings}/quality-gate-configs", UiJsonContext.Default.IReadOnlyListQualityGateConfiguration, ct);

    public Task SaveQualityGateConfigAsync(QualityGateConfiguration config, CancellationToken ct = default) =>
        SendJsonAsync(HttpMethod.Put, $"{Settings}/quality-gate-configs", config, UiJsonContext.Default.QualityGateConfiguration, ct);

    public Task DeleteQualityGateConfigAsync(string id, CancellationToken ct = default) =>
        SendAsync(HttpMethod.Delete, $"{Settings}/quality-gate-configs/{Id(id)}", ct);

    public Task<IReadOnlyList<ReviewerConfiguration>> GetReviewerConfigsAsync(CancellationToken ct = default) =>
        GetAsync($"{Settings}/reviewer-configs", UiJsonContext.Default.IReadOnlyListReviewerConfiguration, ct);

    public Task SaveReviewerConfigAsync(ReviewerConfiguration config, CancellationToken ct = default) =>
        SendJsonAsync(HttpMethod.Put, $"{Settings}/reviewer-configs", config, UiJsonContext.Default.ReviewerConfiguration, ct);

    public Task DeleteReviewerConfigAsync(string id, CancellationToken ct = default) =>
        SendAsync(HttpMethod.Delete, $"{Settings}/reviewer-configs/{Id(id)}", ct);

    public Task ResetReviewerConfigsToDefaultAsync(CancellationToken ct = default) =>
        SendAsync(HttpMethod.Post, $"{Settings}/reviewer-configs/reset-to-defaults", ct);

    public Task<IReadOnlyList<PipelineProject>> GetProjectsAsync(CancellationToken ct = default) =>
        GetAsync($"{Settings}/projects", UiJsonContext.Default.IReadOnlyListPipelineProject, ct);

    public Task<PipelineProject?> GetProjectByIdAsync(string id, CancellationToken ct = default) =>
        FindAsync($"{Settings}/projects/{Id(id)}", UiJsonContext.Default.PipelineProject, ct);

    public Task SaveProjectAsync(PipelineProject project, CancellationToken ct = default) =>
        SendJsonAsync(HttpMethod.Put, $"{Settings}/projects", project, UiJsonContext.Default.PipelineProject, ct);

    public Task DeleteProjectAsync(string id, CancellationToken ct = default) =>
        SendAsync(HttpMethod.Delete, $"{Settings}/projects/{Id(id)}", ct);

    public Task<IReadOnlyList<PipelineJobTemplate>> GetAllTemplatesAsync(CancellationToken ct = default) =>
        GetAsync($"{Settings}/templates", UiJsonContext.Default.IReadOnlyListPipelineJobTemplate, ct);

    public Task MoveTemplateAsync(ProjectId sourceProjectId, ProjectId targetProjectId, string templateId, CancellationToken ct = default) =>
        SendJsonAsync(HttpMethod.Post, $"{Settings}/templates/move",
            new UiMoveTemplateRequest(sourceProjectId.Value, targetProjectId.Value, templateId),
            UiJsonContext.Default.UiMoveTemplateRequest, ct);

    public Task SaveTemplateAsync(ProjectId projectId, PipelineJobTemplate template, CancellationToken ct = default) =>
        SendJsonAsync(HttpMethod.Put, $"{Settings}/projects/{Id(projectId.Value)}/templates", template, UiJsonContext.Default.PipelineJobTemplate, ct);

    public Task DeleteTemplateAsync(ProjectId projectId, TemplateId templateId, CancellationToken ct = default) =>
        SendAsync(HttpMethod.Delete, $"{Settings}/projects/{Id(projectId.Value)}/templates/{Id(templateId.Value)}", ct);

    public async Task<byte[]> ExportConfigAsync(CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{Settings}/export");
        using var response = await SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsByteArrayAsync(ct);
    }

    public async Task ImportConfigAsync(Stream jsonStream, string fileName, CancellationToken ct = default)
    {
        using var content = new MultipartFormDataContent();
        var file = new StreamContent(jsonStream);
        file.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        content.Add(file, "file", fileName);
        await SendAsync(HttpMethod.Post, $"{Settings}/import", ct, content);
    }

    public Task<UiFirstRunState> GetFirstRunStateAsync(CancellationToken ct = default) =>
        GetAsync($"{Settings}/first-run", UiJsonContext.Default.UiFirstRunState, ct);

    public Task DismissFirstRunBannerAsync(CancellationToken ct = default) =>
        SendAsync(HttpMethod.Post, $"{Settings}/first-run/dismiss", ct);
}
