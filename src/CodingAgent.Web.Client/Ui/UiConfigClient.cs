using CodingAgent.Contracts.Ui;

namespace CodingAgent.Web.Ui;

/// <summary>The configuration every role's pages read (spec 051 2.1a).</summary>
public interface IUiConfigClient
{
    /// <summary>The projects the user can read, by name.</summary>
    Task<IReadOnlyList<UiProject>> GetProjectsAsync(CancellationToken ct = default);

    /// <summary>The templates of a project the user can read.</summary>
    Task<IReadOnlyList<UiTemplate>> GetTemplatesAsync(string projectId, CancellationToken ct = default);

    Task<UiPipelineSettings> GetPipelineSettingsAsync(CancellationToken ct = default);
}

public sealed class UiConfigClient(HttpClient http, IUiAccessTokenSource tokens) : UiApiClient(http, tokens), IUiConfigClient
{
    public Task<IReadOnlyList<UiProject>> GetProjectsAsync(CancellationToken ct = default) =>
        GetAsync("/api/ui/projects", UiJsonContext.Default.IReadOnlyListUiProject, ct);

    public Task<IReadOnlyList<UiTemplate>> GetTemplatesAsync(string projectId, CancellationToken ct = default) =>
        GetAsync($"/api/ui/projects/{Uri.EscapeDataString(projectId)}/templates", UiJsonContext.Default.IReadOnlyListUiTemplate, ct);

    public Task<UiPipelineSettings> GetPipelineSettingsAsync(CancellationToken ct = default) =>
        GetAsync("/api/ui/pipeline-settings", UiJsonContext.Default.UiPipelineSettings, ct);
}
