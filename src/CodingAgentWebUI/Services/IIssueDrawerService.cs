using CodingAgentWebUI.Pipeline.Models;

namespace CodingAgentWebUI.Services;

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
    HashSet<(IssueIdentifier IssueIdentifier, ProviderConfigId IssueProviderConfigId)> ActiveIssues { get; }

    // ── Dispatch ──

    Task<(bool Success, string? Error, string? SuccessMessage)> DispatchIssueAsync(
        IssueSummary issue,
        PipelineJobTemplate template,
        IReadOnlyList<ProviderConfig> issueProviders,
        IReadOnlyList<ProviderConfig> repoProviders,
        PipelineProject? parentProject);

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

    Task<(bool Success, string? Error, string? SuccessMessage)> DispatchFromIssueDrawerAsync(
        IssueSummary issue,
        IReadOnlyList<ProviderConfig> issueProviders,
        IReadOnlyList<ProviderConfig> repoProviders,
        PipelineProject? parentProject);

    // ── Active issues ──

    Task RefreshActiveIssuesAsync();
    bool IsIssueActive(IssueIdentifier issueIdentifier, string issueProviderConfigId);
    Task<bool> IsIssueDistributedAsync(string issueIdentifier, string issueProviderConfigId);

    // ── Cross-drawer coordination ──

    void Hide();
}
