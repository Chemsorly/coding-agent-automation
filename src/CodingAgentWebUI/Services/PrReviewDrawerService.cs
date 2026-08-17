using CodingAgentWebUI.Orchestration.Registry;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using Serilog;
using ILogger = Serilog.ILogger;

namespace CodingAgentWebUI.Services;

/// <summary>
/// Manages the PR review dispatch drawer lifecycle: loading pull requests,
/// label filtering, pagination, and dispatch.
/// Owns and constructs the underlying DrawerStateService&lt;PullRequestSummary&gt;.
/// Registered as Scoped (one instance per Blazor circuit).
/// </summary>
public sealed class PrReviewDrawerService : IPrReviewDrawerService, IDisposable
{
    private static readonly ILogger Logger = Log.ForContext<PrReviewDrawerService>();

    private readonly IProviderFactory _providerFactory;
    private readonly IWorkDistributor _workDistributor;
    private readonly IAgentRegistryService _agentRegistry;
    private readonly IDispatchOrchestrationService? _dispatchOrchestration;

    private readonly DrawerStateService<PullRequestSummary> _prDrawer;

    public PrReviewDrawerService(
        IProviderFactory providerFactory,
        IWorkDistributor workDistributor,
        IAgentRegistryService agentRegistry,
        IDispatchOrchestrationService? dispatchOrchestration = null)
    {
        _providerFactory = providerFactory;
        _workDistributor = workDistributor;
        _agentRegistry = agentRegistry;
        _dispatchOrchestration = dispatchOrchestration;

        _prDrawer = new DrawerStateService<PullRequestSummary>(
            t => LoadPrDrawerPageAsync(t, 1),
            LoadPrDrawerLabelsAsync,
            // TODO: [WARNING] This callback throws InvalidOperationException rather than returning a
            // graceful error tuple. Any code path that calls DrawerState.DispatchAsync() directly
            // (e.g., a Blazor component bound to the DrawerState property) will receive an unhandled
            // runtime exception. Consider returning (false, "Use DispatchFromPrDrawerAsync", null)
            // instead of throwing.
            (pr, template) => throw new InvalidOperationException("Use DispatchFromPrDrawerAsync on the coordinator"));
    }

    // ── IPrReviewDrawerService ──

    public DrawerStateService<PullRequestSummary> DrawerState => _prDrawer;

    // ── Data loading ──

    public async Task<string?> LoadPrDrawerPageAsync(PipelineJobTemplate template, int page)
    {
        _prDrawer.Loading = true;
        _prDrawer.Page = page;
        try
        {
            var repoConfig = _cachedRepoProviders?.FirstOrDefault(p => p.Id == template.RepoProviderId);
            if (repoConfig == null) { _prDrawer.Items = new(); _prDrawer.Loading = false; return null; }
            await using var repoProvider = _providerFactory.CreateRepositoryProvider(repoConfig);
            var labels = _prDrawer.SelectedLabels.Count > 0 ? _prDrawer.SelectedLabels : null;
            var result = await repoProvider.ListOpenPullRequestsAsync(page, 15, labels, CancellationToken.None);
            _prDrawer.Items = result.Items.ToList();
            _prDrawer.HasMore = result.HasMore;
            return null;
        }
        catch (Exception ex) { _prDrawer.Items = new(); return $"Failed to load pull requests: {ex.Message}"; }
        finally { _prDrawer.Loading = false; }
    }

    private async Task<string?> LoadPrDrawerLabelsAsync(PipelineJobTemplate template)
    {
        try
        {
            var providerConfig = _cachedIssueProviders?.FirstOrDefault(p => p.Id == template.IssueProviderId);
            if (providerConfig == null) return null;
            await using var provider = _providerFactory.CreateIssueProvider(providerConfig);
            var labels = await provider.ListRepositoryLabelsAsync(CancellationToken.None);
            _prDrawer.Labels = labels.ToList();
            return null;
        }
        catch (Exception ex) { Logger.Warning(ex, "Failed to load PR drawer labels"); _prDrawer.Labels.Clear(); return null; }
    }

    public void ClearPrDrawerLabelFilter()
    {
        // TODO: [WARNING] This delegates entirely to DrawerStateService.ClearItems(). Verify that
        // ClearItems() resets Page, HasMore, and SelectedLabels in addition to clearing Items. The
        // old implementation explicitly reset all four fields. If ClearItems() only clears Items, the
        // Page, HasMore, and SelectedLabels state will be stale after a label filter clear, causing
        // pagination to resume from a non-1 page on the next load. The
        // PrReviewDrawerServiceTests.ClearPrDrawerLabelFilter_ClearsItemsAndPage test guards Page and
        // HasMore but does not assert SelectedLabels.Clear().
        _prDrawer.ClearItems();
    }

    // ── Cached provider context (set at open/switch time) ──

    private IReadOnlyList<ProviderConfig>? _cachedIssueProviders;
    private IReadOnlyList<ProviderConfig>? _cachedRepoProviders;

    // ── Dispatch ──

    public async Task<(bool Success, string? Error, string? SuccessMessage)> DispatchPrReviewAsync(
        PullRequestSummary pr,
        PipelineJobTemplate template,
        IReadOnlyList<ProviderConfig> issueProviders,
        IReadOnlyList<ProviderConfig> repoProviders,
        PipelineProject? parentProject)
    {
        if (_workDistributor.RequiresConnectedAgents && _agentRegistry.GetAllAgents().Count == 0)
            return (false, "Could not dispatch — no agents are currently connected.", null);

        if (_dispatchOrchestration is not null)
        {
            return await DrawerDispatchHelper.DispatchWithOrchestrationAsync(
                _dispatchOrchestration,
                project =>
                {
                    var reviewRequest = new ReviewDispatchRequest
                    {
                        PrIdentifier = pr.Identifier,
                        PrBranchName = pr.BranchName,
                        PrTitle = pr.Title ?? "",
                        PrUrl = pr.Url,
                        PrTargetBranch = pr.TargetBranch,
                        PrDescription = pr.Description,
                        PrAuthor = pr.Author,
                        IssueProviderId = template.IssueProviderId,
                        RepoProviderId = template.RepoProviderId,
                        BrainProviderId = template.BrainProviderId,
                        InitiatedBy = DrawerDispatchHelper.ManualInitiator
                    };
                    return _dispatchOrchestration.PrepareReviewDistributionRequestAsync(
                        reviewRequest, project, CancellationToken.None);
                },
                parentProject ?? new PipelineProject { Id = "", Name = "Unknown" },
                $"PR #{pr.Identifier} is already being processed or queued.",
                $"⏳ Queued PR #{pr.Identifier} for review — waiting for an idle agent",
                $"PR #{pr.Identifier} dispatched for review.");
        }

        var minimalRequest = JobDistributionRequest.FromTemplate(
            template, pr, initiatedBy: DrawerDispatchHelper.ManualInitiator, timeoutSeconds: 3600,
            projectId: parentProject?.Id, projectName: parentProject?.Name);
        return await DrawerDispatchHelper.DispatchLegacyAsync(_workDistributor, minimalRequest,
            $"PR #{pr.Identifier} dispatched for review.",
            $"PR #{pr.Identifier} is already being processed or queued.");
    }

    // ── Drawer orchestration ──

    public async Task<string?> OpenPrDrawerAsync(
        TemplateId templateId,
        IReadOnlyList<PipelineJobTemplate> templates,
        Func<Task>? notifyStateChanged = null)
    {
        var template = templates.FirstOrDefault(t => t.Id == templateId.Value);
        if (template == null) return null;
        return await _prDrawer.OpenAsync(template, notifyStateChanged);
    }

    public void ClosePrDrawer() => _prDrawer.Close();

    public Task<string?> SwitchToPrDrawerAsync(
        TemplateId templateId,
        IReadOnlyList<PipelineJobTemplate> templates,
        Func<Task>? notifyStateChanged = null)
    {
        return _prDrawer.SwitchAsync(templateId, notifyStateChanged,
            () => _prDrawer.Items.Count > 0,
            async (id, ns) =>
            {
                var template = templates.FirstOrDefault(t => t.Id == id);
                if (template == null) return null;
                return template;
            });
    }

    public async Task<(bool Success, string? Error, string? SuccessMessage)> DispatchFromPrDrawerAsync(
        PullRequestSummary pr,
        IReadOnlyList<ProviderConfig> issueProviders,
        IReadOnlyList<ProviderConfig> repoProviders,
        PipelineProject? parentProject)
    {
        _prDrawer.IsDispatching = true;
        try
        {
            if (_prDrawer.Template == null)
                return (false, "No template selected. Please select a template first.", null);

            return await DispatchPrReviewAsync(pr, _prDrawer.Template, issueProviders, repoProviders, parentProject);
        }
        finally { _prDrawer.IsDispatching = false; }
    }

    // ── Cross-drawer coordination ──

    public void Hide() => _prDrawer.IsOpen = false;

    // ── Provider context injection (called by coordinator before open/switch) ──

    internal void SetProviderContext(
        IReadOnlyList<ProviderConfig> issueProviders,
        IReadOnlyList<ProviderConfig> repoProviders)
    {
        _cachedIssueProviders = issueProviders;
        _cachedRepoProviders = repoProviders;
    }

    // ── IDisposable ──

    private bool _disposed;

    public void Dispose()
    {
        if (_disposed) return;
        _prDrawer.Dispose();
        _disposed = true;
    }
}
