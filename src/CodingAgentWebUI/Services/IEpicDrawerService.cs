using CodingAgentWebUI.Pipeline.Models;

namespace CodingAgentWebUI.Services;

/// <summary>
/// Manages the epic dispatch drawer lifecycle: loading epics, label filtering,
/// pagination, and dispatch (decomposition analysis or decomposition).
/// Owns the underlying DrawerStateService&lt;IssueSummary&gt; for epic items.
/// </summary>
public interface IEpicDrawerService
{
    /// <summary>The underlying DrawerStateService&lt;IssueSummary&gt; (exposed for direct state access).</summary>
    DrawerStateService<IssueSummary> DrawerState { get; }

    // ── Data loading ──

    Task<string?> LoadEpicDrawerIssuesAsync(PipelineJobTemplate template, int page = 1);
    void ClearEpicDrawerIssues();

    // ── Dispatch ──

    Task<(bool Success, string? Error, string? SuccessMessage)> DispatchDecompositionAsync(
        IssueSummary issue,
        PipelineJobTemplate template,
        IReadOnlyList<ProviderConfig> issueProviders,
        IReadOnlyList<ProviderConfig> repoProviders,
        PipelineProject? parentProject);

    // ── Drawer orchestration ──

    Task<string?> OpenEpicDrawerAsync(
        TemplateId templateId,
        IReadOnlyList<PipelineJobTemplate> templates,
        IReadOnlyList<PipelineProject> projects,
        Func<Task>? notifyStateChanged = null);

    void CloseEpicDrawer();

    Task<string?> SwitchToEpicDrawerAsync(
        TemplateId templateId,
        IReadOnlyList<PipelineJobTemplate> templates,
        IReadOnlyList<PipelineProject> projects,
        Func<Task>? notifyStateChanged = null);

    Task<(bool Success, string? Error, string? SuccessMessage)> DispatchFromEpicDrawerAsync(
        IssueSummary issue,
        IReadOnlyList<ProviderConfig> issueProviders,
        IReadOnlyList<ProviderConfig> repoProviders,
        PipelineProject? parentProject);

    // ── Cross-drawer coordination ──

    void Hide();
}
