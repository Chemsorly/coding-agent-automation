using CodingAgentWebUI.Pipeline.Models;

namespace CodingAgentWebUI.Services;

/// <summary>
/// Manages the PR review dispatch drawer lifecycle: loading pull requests,
/// label filtering, pagination, and dispatch.
/// Owns the underlying DrawerStateService&lt;PullRequestSummary&gt;.
/// </summary>
public interface IPrReviewDrawerService
{
    /// <summary>The underlying DrawerStateService&lt;PullRequestSummary&gt; (exposed for direct state access).</summary>
    DrawerStateService<PullRequestSummary> DrawerState { get; }

    // ── Data loading ──

    Task<string?> LoadPrDrawerPageAsync(PipelineJobTemplate template, int page);
    void ClearPrDrawerLabelFilter();

    // ── Dispatch ──

    Task<(bool Success, string? Error, string? SuccessMessage)> DispatchPrReviewAsync(
        PullRequestSummary pr,
        PipelineJobTemplate template,
        IReadOnlyList<ProviderConfig> issueProviders,
        IReadOnlyList<ProviderConfig> repoProviders,
        PipelineProject? parentProject);

    // ── Drawer orchestration ──

    Task<string?> OpenPrDrawerAsync(
        TemplateId templateId,
        IReadOnlyList<PipelineJobTemplate> templates,
        Func<Task>? notifyStateChanged = null);

    void ClosePrDrawer();

    Task<string?> SwitchToPrDrawerAsync(
        TemplateId templateId,
        IReadOnlyList<PipelineJobTemplate> templates,
        Func<Task>? notifyStateChanged = null);

    Task<(bool Success, string? Error, string? SuccessMessage)> DispatchFromPrDrawerAsync(
        PullRequestSummary pr,
        IReadOnlyList<ProviderConfig> issueProviders,
        IReadOnlyList<ProviderConfig> repoProviders,
        PipelineProject? parentProject);

    // ── Cross-drawer coordination ──

    void Hide();
}
