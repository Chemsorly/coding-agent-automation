using CodingAgent.Pipeline.Models;

namespace CodingAgent.Web.Services;

/// <summary>
/// Manages the issue dispatch drawer lifecycle: loading issues, dependency checking,
/// label filtering, pagination, and dispatch. Owns the underlying DrawerStateService&lt;IssueSummary&gt;.
/// </summary>
public interface IIssueDrawerService
{
    /// <summary>The underlying DrawerStateService&lt;IssueSummary&gt; (exposed for direct state access and CancellationToken).</summary>
    DrawerStateService<IssueSummary> DrawerState { get; }

    // ── Data loading ──

    Task<string?> LoadDrawerIssuesAsync(PipelineJobTemplate template, int page);
    Task<string?> LoadDrawerIssuesPageAsync(PipelineJobTemplate template, int page);
    Task<string?> LoadDrawerLabelsAsync(PipelineJobTemplate template);
    Task CheckDrawerDependenciesAsync(PipelineJobTemplate template, Action? onProgress, CancellationToken cancellationToken);
    void ClearDrawerIssues();

    // ── State ──

    Dictionary<string, DependencyCheckResult> DrawerReadiness { get; }
    IReadOnlyDictionary<(IssueIdentifier IssueIdentifier, ProviderConfigId IssueProviderConfigId), WorkItemStatus> ActiveIssues { get; }

    // ── Dispatch ──

    /// <summary>Dispatches the issue of the template through the API; the error or success message is shown as it is.</summary>
    Task<(bool Success, string? Error, string? SuccessMessage)> DispatchIssueAsync(IssueSummary issue, PipelineJobTemplate template);

    // ── Drawer orchestration ──

    Task<string?> OpenIssueDrawerAsync(
        TemplateId templateId,
        IReadOnlyList<PipelineJobTemplate> templates,
        Func<Task>? notifyStateChanged = null);

    void CloseIssueDrawer();

    Task<string?> SwitchToIssueDrawerAsync(
        TemplateId templateId,
        IReadOnlyList<PipelineJobTemplate> templates,
        Func<Task>? notifyStateChanged = null);

    Task<(bool Success, string? Error, string? SuccessMessage)> DispatchFromIssueDrawerAsync(IssueSummary issue);

    // ── Active issues ──

    Task RefreshActiveIssuesAsync(TemplateId templateId, CancellationToken ct = default);
    bool IsIssueActive(IssueIdentifier issueIdentifier, ProviderConfigId issueProviderConfigId);
    WorkItemStatus? GetIssueWorkItemStatus(IssueIdentifier issueIdentifier, ProviderConfigId issueProviderConfigId);

    // ── Cross-drawer coordination ──

    void Hide();
}
