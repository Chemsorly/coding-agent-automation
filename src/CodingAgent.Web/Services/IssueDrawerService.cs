using CodingAgent.Api.Client;
using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;
using CodingAgent.Pipeline.Services;
using Serilog;
using ILogger = Serilog.ILogger;

namespace CodingAgent.Web.Services;

/// <summary>
/// Manages the issue dispatch drawer lifecycle: loading issues, dependency checking,
/// label filtering, pagination, active-issue tracking, and dispatch.
/// Owns and constructs the underlying DrawerStateService&lt;IssueSummary&gt;.
/// Registered as Scoped (one instance per Blazor circuit).
/// </summary>
public sealed class IssueDrawerService : IIssueDrawerService, IDisposable
{
    private static readonly ILogger Logger = Log.ForContext<IssueDrawerService>();

    private readonly IProviderFactory _providerFactory;
    private readonly IDependencyChecker _dependencyChecker;
    private readonly IWorkDistributor _workDistributor;
    private readonly IPipelineApiWorkItemClient _apiClient;
    private readonly IDispatchOrchestrationService _dispatchOrchestration;

    private readonly DrawerStateService<IssueSummary> _issueDrawer;

    public IssueDrawerService(
        IProviderFactory providerFactory,
        IDependencyChecker dependencyChecker,
        IWorkDistributor workDistributor,
        IDispatchOrchestrationService dispatchOrchestration,
        IPipelineApiWorkItemClient apiClient)
    {
        _providerFactory = providerFactory;
        _dependencyChecker = dependencyChecker;
        _workDistributor = workDistributor;
        _apiClient = apiClient;
        _dispatchOrchestration = dispatchOrchestration;

        _issueDrawer = new DrawerStateService<IssueSummary>(
            LoadDrawerIssuesCallbackAsync,
            LoadDrawerLabelsCallbackAsync,
            // TODO: [WARNING] This callback throws InvalidOperationException rather than returning a
            // graceful error tuple. Any code path that calls DrawerState.DispatchAsync() directly
            // (e.g., a Blazor component bound to the DrawerState property) will receive an unhandled
            // runtime exception with no indication of the correct call path. Consider returning
            // (false, "Use DispatchFromIssueDrawerAsync", null) instead of throwing.
            (issue, template) => throw new InvalidOperationException("Use DispatchFromIssueDrawerAsync on the coordinator"),
            closeOnDispatch: true,
            postLoadAsync: CheckDrawerDependenciesInBackgroundAsync);
    }

    // ── IIssueDrawerService ──

    public DrawerStateService<IssueSummary> DrawerState => _issueDrawer;

    public Dictionary<string, DependencyCheckResult> DrawerReadiness { get; private set; } = new();

    /// <summary>
    /// Status-aware map of active issues. Keys are (IssueIdentifier, IssueProviderConfigId) tuples;
    /// values are the WorkItemStatus of the corresponding work item (Pending = Queued, Running/Dispatched = Running).
    /// Populated by <see cref="RefreshActiveIssuesAsync"/>.
    /// </summary>
    public IReadOnlyDictionary<(IssueIdentifier IssueIdentifier, ProviderConfigId IssueProviderConfigId), WorkItemStatus> ActiveIssues { get; private set; } =
        new Dictionary<(IssueIdentifier, ProviderConfigId), WorkItemStatus>();

    // ── Data loading ──

    /// <summary>Private 1-arg callback passed to DrawerStateService (page=1 load).</summary>
    private async Task<string?> LoadDrawerIssuesCallbackAsync(PipelineJobTemplate template)
        => await LoadDrawerIssuesAsync(template, 1);

    public async Task<string?> LoadDrawerIssuesAsync(PipelineJobTemplate template, int page)
    {
        _issueDrawer.Loading = true;
        _issueDrawer.Page = page;
        var ct = _issueDrawer.CancellationToken;
        try
        {
            var providerConfig = _cachedIssueProviders?.FirstOrDefault(p => p.Id == template.IssueProviderId);
            if (providerConfig == null) { _issueDrawer.Loading = false; return "Issue provider not found for this template."; }
            await using var provider = _providerFactory.CreateIssueProvider(providerConfig);
            var labels = _issueDrawer.SelectedLabels.Count > 0 ? _issueDrawer.SelectedLabels : null;
            var result = await provider.ListOpenIssuesAsync(_issueDrawer.Page, 15, labels, ct);
            _issueDrawer.Items = result.Items.ToList();
            _issueDrawer.HasMore = result.HasMore;
            return null;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            _issueDrawer.Items.Clear();
            return null;
        }
        catch (Exception ex) { _issueDrawer.Items.Clear(); return $"Failed to load issues: {ex.Message}"; }
        finally { _issueDrawer.Loading = false; }
    }

    public Task<string?> LoadDrawerIssuesPageAsync(PipelineJobTemplate template, int page)
        => LoadDrawerIssuesAsync(template, page);

    private async Task<string?> LoadDrawerLabelsCallbackAsync(PipelineJobTemplate template)
    {
        var ct = _issueDrawer.CancellationToken;
        try
        {
            var providerConfig = _cachedIssueProviders?.FirstOrDefault(p => p.Id == template.IssueProviderId);
            if (providerConfig == null) return null;
            await using var provider = _providerFactory.CreateIssueProvider(providerConfig);
            var labels = await provider.ListRepositoryLabelsAsync(ct);
            _issueDrawer.Labels = labels.ToList();
            return null;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            _issueDrawer.Labels.Clear();
            return null;
        }
        catch
        {
            _issueDrawer.Labels.Clear();
            return null;
        }
    }

    public Task<string?> LoadDrawerLabelsAsync(PipelineJobTemplate template)
        => LoadDrawerLabelsCallbackAsync(template);

    public async Task CheckDrawerDependenciesAsync(
        PipelineJobTemplate template,
        Action? onProgress,
        CancellationToken cancellationToken)
    {
        var providerConfig = _cachedIssueProviders?.FirstOrDefault(p => p.Id == template.IssueProviderId);
        if (providerConfig == null) return;

        var issues = _issueDrawer.Items.ToList();
        var stateCache = new Dictionary<int, bool>();

        try
        {
            await using var provider = _providerFactory.CreateIssueProvider(providerConfig);
            foreach (var issue in issues)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var result = await _dependencyChecker.CheckAsync(
                    issue.Identifier, issue.Description, provider, stateCache, cancellationToken);
                DrawerReadiness[issue.Identifier] = result;
                onProgress?.Invoke();
            }
        }
        catch (OperationCanceledException) { throw; }
        catch
        {
            // Best-effort: partial results are still useful
        }
    }

    private async Task CheckDrawerDependenciesInBackgroundAsync(PipelineJobTemplate template, CancellationToken ct)
    {
        try
        {
            await CheckDrawerDependenciesAsync(template, null, ct);
        }
        catch (OperationCanceledException) { /* expected on drawer close */ }
    }

    public void ClearDrawerIssues()
    {
        _issueDrawer.Items.Clear();
        _issueDrawer.Page = 1;
        _issueDrawer.HasMore = false;
        DrawerReadiness.Clear();
        _issueDrawer.SelectedLabels.Clear();
    }

    // ── Cached provider context (set at open/switch time) ──

    private IReadOnlyList<ProviderConfig>? _cachedIssueProviders;

    // ── Dispatch ──

    public async Task<(bool Success, string? Error, string? SuccessMessage)> DispatchIssueAsync(
        IssueSummary issue,
        PipelineJobTemplate template,
        IReadOnlyList<ProviderConfig> issueProviders,
        IReadOnlyList<ProviderConfig> repoProviders,
        PipelineProject? parentProject)
    {
        if (!issueProviders.Any(p => p.Id == template.IssueProviderId) || !repoProviders.Any(p => p.Id == template.RepoProviderId))
            return (false, "Template references providers that no longer exist.", null);

        var depProviderConfig = issueProviders.FirstOrDefault(p => p.Id == template.IssueProviderId);
        if (depProviderConfig != null)
        {
            await using var issueProvider = _providerFactory.CreateIssueProvider(depProviderConfig);
            // TODO: The single-provider CheckAsync overload is used here. It forwards to the
            // cross-tracker overload with empty allProviders/providerUrlPrefixes, which means
            // any URL-based cross-tracker dependency (e.g. "Depends on https://github.com/acme/api/issues/40"
            // written by the decomposition step) will always be logged as "No configured issue
            // provider matches URL dependency" and treated as unresolved — permanently blocking
            // dispatch from the drawer even after the dependency closes. Fix: pass all configured
            // issue providers and their URL prefixes (derived from issueProviders) to the full
            // cross-tracker overload so URL deps are resolved correctly here too.
            var depResult = await _dependencyChecker.CheckAsync(issue.Identifier, issue.Description, issueProvider, new Dictionary<int, bool>(), CancellationToken.None);
            if (!depResult.IsReady)
            {
                var allBlocked = depResult.BlockedBy.Select(n => $"#{n}")
                    .Concat(depResult.BlockedByUrls ?? []);
                return (false, $"Cannot dispatch — issue is blocked by open dependencies: {string.Join(", ", allBlocked)}", null);
            }
        }

        // EpicReview is a terminal state in the epic decomposition workflow. Unlike other terminal
        // labels (agent:done, agent:cancelled, etc.) which represent completed or aborted
        // implementation runs and can be force-requeued via manual dispatch, agent:epic-review marks
        // an issue that is awaiting human approval of the epic plan. Re-dispatching it as an
        // implementation job would bypass the approval gate entirely. Reject early so the UI shows
        // a clean error rather than a confusing 409 from the database unique-index constraint.
        if (issue.Labels.Any(l => StringComparer.OrdinalIgnoreCase.Equals(l, AgentLabels.EpicReview)))
            return (false, $"Cannot dispatch — issue is in state '{AgentLabels.EpicReview}' which requires human approval before it can proceed. Use the epic approval workflow instead.", null);

        // Manual dispatch is an explicit force-requeue: clear any blocking labels and set agent:next
        // before creating the WorkItem. Without this, DispatchLoop would see the blocking label,
        // cancel the WorkItem, and re-stamp the same label — a self-reinforcing cancellation loop.
        // Only fires when the issue actually carries a blocking label (no-op for clean issues).
        //
        // Note on multiple blocking labels: FirstOrDefault picks the first one as expectedCurrentLabel
        // for LabelStateMachine logging. AgentLabelOperations.SwapAsync iterates AgentLabels.All and
        // removes every agent label except agent:next, so ALL blocking labels are cleared regardless
        // of which one is passed as expectedCurrentLabel. The pipeline invariant is that at most one
        // agent:* label should be present at a time; additional blocking labels from partial-swap
        // failures are still removed by the full sweep.
        var blockingLabel = issue.Labels.FirstOrDefault(l => AgentLabels.DispatchIneligibleLabels.Contains(l));
        if (blockingLabel != null)
        {
            if (depProviderConfig == null)
            {
                // The template's issue provider is absent from the caller's provider list.
                // The WorkItem will be created but DispatchLoop will fail-open (not cancel) on the
                // missing config — the job will never start. Log a warning so this is visible in
                // the operator UI rather than a silent no-dispatch.
                Logger.Warning(
                    "IssueDrawerService: manual dispatch on issue {IssueIdentifier} has blocking label {BlockingLabel} " +
                    "but issue provider config for template {TemplateId} is not available — cannot clear label before dispatch. " +
                    "The WorkItem will be created but the job may not start until the label is cleared manually.",
                    issue.Identifier, blockingLabel, template.Id);
            }
            else
            {
                try
                {
                    await using var issueProvider = _providerFactory.CreateIssueProvider(depProviderConfig);
                    Logger.Information(
                        "IssueDrawerService: manual dispatch — clearing blocking label {BlockingLabel} and setting agent:next on issue {IssueIdentifier}",
                        blockingLabel, issue.Identifier);
                    await AgentLabelOperations.SwapAsync(
                        removeLabel: (label, ct) => issueProvider.RemoveLabelAsync(issue.Identifier, label, ct),
                        addLabel: (label, ct) => issueProvider.AddLabelAsync(issue.Identifier, label, ct),
                        newLabel: AgentLabels.Next,
                        ct: CancellationToken.None,
                        options: new LabelSwapOptions
                        {
                            ExpectedCurrentLabel = blockingLabel,
                            Identifier = issue.Identifier
                        });
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    Logger.Error(ex,
                        "IssueDrawerService: failed to clear blocking label {BlockingLabel} on issue {IssueIdentifier} before dispatch",
                        blockingLabel, issue.Identifier);
                    return (false, $"Could not clear blocking label '{blockingLabel}' before dispatch — {ex.Message}", null);
                }
            }
        }

        return await DrawerDispatchHelper.DispatchWithOrchestrationAsync(
            _dispatchOrchestration,
            project => _dispatchOrchestration.PrepareDistributionRequestAsync(
                new ImplementationDispatchOrchestrationRequest
                {
                    IssueIdentifier = issue.Identifier,
                    IssueProviderId = template.IssueProviderId,
                    RepoProviderId = template.RepoProviderId,
                    BrainProviderId = template.BrainProviderId,
                    PipelineProviderId = template.PipelineProviderId,
                    InitiatedBy = DrawerDispatchHelper.ManualInitiator,
                    Project = project
                }, CancellationToken.None),
            parentProject ?? new PipelineProject { Id = "", Name = "Unknown" },
            "Could not dispatch — distribution failed.",
            $"⏳ Queued #{issue.Identifier} — the job controller will start an agent pod for it",
            $"✅ Dispatched #{issue.Identifier}");
    }

    // ── Drawer orchestration ──

    public async Task<string?> OpenIssueDrawerAsync(
        TemplateId templateId,
        IReadOnlyList<PipelineJobTemplate> templates,
        Func<Task>? notifyStateChanged = null)
    {
        var template = templates.FirstOrDefault(t => t.Id == templateId.Value);
        if (template == null) return null;
        await RefreshActiveIssuesAsync();
        return await _issueDrawer.OpenAsync(template, notifyStateChanged);
    }

    public void CloseIssueDrawer()
    {
        _issueDrawer.Close();
        DrawerReadiness.Clear();
    }

    public Task<string?> SwitchToIssueDrawerAsync(
        TemplateId templateId,
        IReadOnlyList<PipelineJobTemplate> templates,
        Func<Task>? notifyStateChanged = null)
    {
        return _issueDrawer.SwitchAsync(templateId, notifyStateChanged,
            () => _issueDrawer.Items.Count > 0,
            async (id, ns) =>
            {
                var template = templates.FirstOrDefault(t => t.Id == id);
                if (template == null) return null;
                await RefreshActiveIssuesAsync();
                return template;
            });
    }

    public async Task<(bool Success, string? Error, string? SuccessMessage)> DispatchFromIssueDrawerAsync(
        IssueSummary issue,
        IReadOnlyList<ProviderConfig> issueProviders,
        IReadOnlyList<ProviderConfig> repoProviders,
        PipelineProject? parentProject)
    {
        // Cache providers so the DrawerStateService dispatch callback can reach them
        _cachedIssueProviders = issueProviders;

        _issueDrawer.IsDispatching = true;
        try
        {
            if (_issueDrawer.Template == null)
                return (false, "No template selected. Please select a template first.", null);

            var (success, error, successMessage) = await DispatchIssueAsync(issue, _issueDrawer.Template, issueProviders, repoProviders, parentProject);
            if (success)
                _issueDrawer.Close();
            return (success, error, successMessage);
        }
        finally { _issueDrawer.IsDispatching = false; }
    }

    // ── Active issues ──

    // TODO: [WARNING] RefreshActiveIssuesAsync passes CancellationToken.None to both API calls because
    //   the method signature accepts no CancellationToken. Callers that own a cancellation token
    //   (e.g. component disposal, navigation-away) cannot forward it, so the two in-flight HTTP
    //   requests run to completion even after the owning service/component is disposed or navigated
    //   away, then write into a stale ActiveIssues dictionary. Consider adding a CancellationToken
    //   parameter to RefreshActiveIssuesAsync and threading it through both API calls.
    public async Task RefreshActiveIssuesAsync()
    {
        // Build a status-aware map by combining:
        //   1. GetActiveIdentifiersAsync — all (IssueIdentifier, ProviderConfigId) pairs with active or
        //      recently-terminal WorkItems. Items in this set are Dispatched/Running unless also pending.
        //   2. GetPendingAsync — Pending (Queued) items, which include both IssueIdentifier AND
        //      IssueProviderConfigId (ActiveWorkItemDto lacks IssueProviderConfigId so we cannot use
        //      GetActiveAsync for the full key).
        // Strategy:
        //   - All pending items → WorkItemStatus.Pending (Queued badge)
        //   - All active-identifier items not in the pending set → WorkItemStatus.Running (Running badge)
        //     (these are Dispatched or Running status on the server; both show as "Running" in the UI)
        // TODO: GetActiveIdentifiersAsync returns pairs for recently-terminal work items (Succeeded/Failed/
        //   Cancelled within DefaultRestartDedupCooldown) in addition to truly-active ones. A recently-failed
        //   issue that is not in the pending set is classified as Running here, showing a false "Running" badge
        //   in the dispatch drawer. Consider excluding recently-terminal pairs from the Running classification
        //   (e.g. cross-check against GetActiveAsync which exposes the actual WorkItemStatus).
        var activeIdentifiers = await _apiClient.GetActiveIdentifiersAsync(CancellationToken.None);
        // TODO: GetPendingAsync is capped at maxResults:200. If more than 200 work items are Pending, items
        //   beyond the cap are absent from pendingKeys and are misclassified as Running. Consider paging until
        //   exhausted or raising the cap to exceed the realistic maximum queue depth.
        var pendingItems = await _apiClient.GetPendingAsync(maxResults: 200, ct: CancellationToken.None);

        var pendingKeys = pendingItems
            .Select(p => ((IssueIdentifier)p.IssueIdentifier, (ProviderConfigId)p.IssueProviderConfigId))
            .ToHashSet();

        var map = new Dictionary<(IssueIdentifier IssueIdentifier, ProviderConfigId IssueProviderConfigId), WorkItemStatus>();
        foreach (var (issueId, provId) in activeIdentifiers)
        {
            var key = ((IssueIdentifier)issueId, (ProviderConfigId)provId);
            map[key] = pendingKeys.Contains(key) ? WorkItemStatus.Pending : WorkItemStatus.Running;
        }

        ActiveIssues = map;
    }

    /// <summary>
    /// Returns the WorkItemStatus of the issue's current work item, or null if the issue has no
    /// active work item. <see cref="WorkItemStatus.Pending"/> means "Queued";
    /// <see cref="WorkItemStatus.Running"/> means "Running or Dispatched".
    /// </summary>
    public WorkItemStatus? GetIssueWorkItemStatus(IssueIdentifier issueIdentifier, ProviderConfigId issueProviderConfigId)
    {
        var key = (issueIdentifier, issueProviderConfigId);
        return ActiveIssues.TryGetValue(key, out var status) ? status : null;
    }

    public bool IsIssueActive(IssueIdentifier issueIdentifier, ProviderConfigId issueProviderConfigId)
        => ActiveIssues.ContainsKey((issueIdentifier, issueProviderConfigId));

    public Task<bool> IsIssueDistributedAsync(IssueIdentifier issueIdentifier, ProviderConfigId issueProviderConfigId)
        => _workDistributor.IsIssueDistributedAsync(issueIdentifier, issueProviderConfigId, CancellationToken.None);

    // ── Cross-drawer coordination ──

    public void Hide() => _issueDrawer.IsOpen = false;

    // ── Provider context injection (called by coordinator before open/switch) ──

    internal void SetProviderContext(
        IReadOnlyList<ProviderConfig> issueProviders,
        IReadOnlyList<ProviderConfig> repoProviders)
    {
        _cachedIssueProviders = issueProviders;
    }

    // ── IDisposable ──

    private bool _disposed;

    public void Dispose()
    {
        if (_disposed) return;
        _issueDrawer.Dispose();
        _disposed = true;
    }
}
