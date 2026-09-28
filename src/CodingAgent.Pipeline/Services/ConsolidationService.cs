using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;
using CodingAgent.Pipeline.Telemetry;
using Serilog;

namespace CodingAgent.Pipeline.Services;

/// <summary>
/// Manages consolidation loop execution: triggering runs, tracking history,
/// persisting run records, and managing harness suggestions.
/// </summary>
public sealed class ConsolidationService : IConsolidationService, IConsolidationRunTracker
{
    private readonly ILogger _logger;
    private readonly PipelineConfiguration _config;
    private readonly IConsolidationRunStore _runStore;
    private readonly IHarnessSuggestionStore _harnessSuggestionStore;
    private readonly IConsolidationWorkspaceManager _workspaceManager;
    private readonly IConsolidationFeedbackCache _feedbackCache;
    private readonly ConsolidationTemplateResolver _templateResolver;
    private readonly IProviderConfigStore _providerConfigStore;
    // TODO [WARNING]: _projectStore is assigned but never read after the constructor.
    // _templateResolver already holds its own reference to deps.ProjectStore (see constructor line).
    // This dead field adds confusion and suggests a refactor was incompletely applied. Consider
    // removing it once it is confirmed no future code path requires direct access. (review-findings-dotnetspecialist.md)
    private readonly IProjectStore _projectStore;
    private readonly IWorkDistributor? _workDistributor;
    private readonly IConsolidationSelectorResolver? _selectorResolver;

    /// <inheritdoc />
    public event Action? OnChange;

    /// <inheritdoc />
    // TODO [WARNING]: Sync-over-async via .GetAwaiter().GetResult(). The underlying store
    // (ApiBackedConsolidationRunStore) issues an HTTP call, making this a blocking network call on a
    // thread-pool thread. No current production caller exists, but IConsolidationService is injected
    // into Blazor components and any future synchronous call site on a Blazor Server circuit thread
    // (where a SynchronizationContext is present) risks a classic deadlock. Prefer converting the
    // interface to Task<bool> IsRunActiveAsync(RunId, CancellationToken), or document the restriction
    // in the interface XML doc if the sync surface must be preserved.
    // (review-findings-dotnetspecialist.md, review-findings-securityreviewer.md)
    public bool IsRunActive(RunId runId)
    {
        // _runningRuns removed (issue #3027): query the store synchronously.
        // This method has no production callers; synchronous wrapper is acceptable.
        var run = _runStore.GetByIdAsync(runId, CancellationToken.None).GetAwaiter().GetResult();
        return run is not null && !IsTerminalStatus(run.Status);
    }

    /// <inheritdoc />
    // TODO [WARNING]: Same sync-over-async pattern as IsRunActive above. The IConsolidationService
    // XML doc comment states this is "Used by ReconciliationService (JobController) to detect stuck
    // consolidation runs" — if that description is made accurate and a background service calls this,
    // the .GetAwaiter().GetResult() call will deadlock or starve the thread pool under load.
    // (review-findings-dotnetspecialist.md, review-findings-securityreviewer.md)
    public DateTimeOffset? GetActiveRunStartedAt(RunId runId)
    {
        // _runningRuns removed (issue #3027): query the store synchronously.
        // This method has no production callers; synchronous wrapper is acceptable.
        var run = _runStore.GetByIdAsync(runId, CancellationToken.None).GetAwaiter().GetResult();
        return run is null || IsTerminalStatus(run.Status) ? null : run.StartedAtUtc;
    }

    public ConsolidationService(
        ConsolidationServiceDependencies deps)
    {
        ArgumentNullException.ThrowIfNull(deps);
        ArgumentNullException.ThrowIfNull(deps.Logger);
        ArgumentNullException.ThrowIfNull(deps.Config);
        ArgumentNullException.ThrowIfNull(deps.ProjectStore);
        ArgumentNullException.ThrowIfNull(deps.RunHistoryService);
        ArgumentNullException.ThrowIfNull(deps.RunStore);
        ArgumentNullException.ThrowIfNull(deps.HarnessSuggestionStore);

        _logger = deps.Logger;
        _config = deps.Config;
        _runStore = deps.RunStore;
        _harnessSuggestionStore = deps.HarnessSuggestionStore;
        _workspaceManager = deps.WorkspaceManager ?? new ConsolidationWorkspaceManager(deps.Logger, deps.Config);
        _feedbackCache = deps.FeedbackCache ?? new ConsolidationFeedbackCache(deps.Logger, deps.RunStore, deps.RunHistoryService);
        _templateResolver = new ConsolidationTemplateResolver(deps.ProjectStore);
        _providerConfigStore = deps.ProviderConfigStore;
        _projectStore = deps.ProjectStore;
        _workDistributor = deps.WorkDistributor;
        _selectorResolver = deps.SelectorResolver;
    }

    /// <inheritdoc />
    public async Task CleanupOrphanedRunsAsync(IReadOnlyCollection<string> activeAgentJobIds, CancellationToken ct)
    {
        var allRuns = await _runStore.LoadAllRunsAsync(ct);
        // Only consider runs that are in Running state — already-terminal runs (Succeeded, Failed,
        // Cancelled) must never be overwritten, even if they happened to be Running at the last
        // shutdown. The API may have updated them to a terminal state after the orchestrator went down.
        foreach (var run in allRuns.Where(r => r.Status == ConsolidationRunStatus.Running))
        {
            // Skip runs where an agent is still actively working — the agent connects to
            // the API hub which survives orchestrator restarts, so the run is not orphaned.
            if (activeAgentJobIds.Contains(run.RunId))
            {
                _logger.Information("Consolidation run {RunId} ({Type}) has active agent — skipping orphan cleanup", run.RunId, run.Type);
                continue;
            }

            // ConsolidationRuns store writes stopped (issue #3028). The PipelineRun row is
            // the authoritative terminal-state record (written by RunLifecycleManager).
            // Mark the in-memory run as Failed for any local callers that query this object,
            // but do NOT write to the ConsolidationRuns store.
            run.Status = ConsolidationRunStatus.Failed;
            run.Summary = "Orphaned: application restarted before completion";
            run.CompletedAtUtc = DateTimeOffset.UtcNow;
            _logger.Information("Marked orphaned consolidation run {RunId} ({Type}) as Failed (store write skipped, issue #3028)", run.RunId, run.Type);
        }

        // _runningRuns removed (issue #3027): the DB-layer partial unique index on
        // (IssueIdentifier, IssueProviderConfigId) is now the authoritative dedup guard.
        // Pending runs no longer need to be rehydrated into an in-memory dictionary;
        // a duplicate TriggerAsync call will hit the 409 path in DistributeAsync instead.
        foreach (var run in allRuns.Where(r => r.Status == ConsolidationRunStatus.Pending))
        {
            _logger.Information(
                "Consolidation run {RunId} ({Type}) is Pending from previous session — WorkItem already in DB queue (not re-dispatched)",
                run.RunId, run.Type);
        }
    }

    /// <inheritdoc />
    public async Task<ConsolidationRun?> TriggerAsync(
        ConsolidationRunType type,
        TemplateId? templateId,
        CancellationToken ct,
        bool autoDispatch = false)
    {
        var templateIdValue = templateId?.Value;

        // ── 1. Resolve template + project ────────────────────────────────────
        string? templateName;
        string? projectName = null;
        string? projectId = null;
        ProviderConfig? repoConfig = null;
        string repoProviderId = "";
        string? brainProviderId = null;

        if (templateId is not null)
        {
            var (template, resolvedProjectName, resolvedProjectId) = await _templateResolver.ResolveTemplateWithProjectAsync(templateIdValue!, ct);
            if (template is null)
            {
                _logger.Warning("Consolidation run rejected: template {TemplateId} not found", templateIdValue);
                return null;
            }
            templateName = template.Name;
            projectName = resolvedProjectName;
            projectId = resolvedProjectId;

            // Resolve repo ProviderConfig (for selector resolution).
            repoConfig = await _providerConfigStore.GetProviderConfigByIdAsync(
                template.RepoProviderId, ProviderKind.Repository, ct);

            // Resolve repoProviderId and brainProviderId for the JobDistributionRequest payload.
            // AgentTokenRefreshService reads RepoProviderConfigId directly from WorkItems.Payload
            // (JSONB) and will fail with HubException if it is empty for template-scoped runs.
            repoProviderId = template.RepoProviderId ?? "";
            brainProviderId = template.BrainProviderId;
        }
        else
        {
            templateName = "Global";
        }

        // ── 2. Guard: WorkDistributor required ───────────────────────────────
        if (_workDistributor is null)
        {
            // Should never happen in production (AddConsolidationServices always injects it).
            // In tests that don't provide a distributor, this surfaces a clear diagnostic.
            _logger.Error(
                "ConsolidationService: IWorkDistributor is not configured — cannot dispatch run for {Type}/{TemplateId}",
                type, templateIdValue ?? "Global");
            throw new InvalidOperationException(
                "ConsolidationService requires IWorkDistributor to be injected via ConsolidationServiceDependencies. " +
                "Ensure AddConsolidationServices passes WorkDistributor.");
        }

        // ── 3. Resolve agent selector labels ─────────────────────────────────
        IReadOnlyList<string>? selectorLabels;
        if (_selectorResolver is not null)
        {
            selectorLabels = await _selectorResolver.ResolveAsync(repoConfig, _config, ct);
            if (selectorLabels is null)
            {
                // Null = startup race (no profiles available yet). Treat as transient failure.
                _logger.Warning(
                    "ConsolidationService: no agent profiles available for {Type}/{TemplateId} — " +
                    "re-trigger after profiles are loaded",
                    type, templateIdValue ?? "Global");
                return null;
            }
        }
        else
        {
            // Fallback when no resolver is injected (tests, or legacy call sites).
            // Use LabelResolver which reads from repoConfig + DefaultRequiredAgentLabels.
            selectorLabels = LabelResolver.ResolveRequiredLabels(repoConfig, _config);
        }

        // ── 4. Build the ConsolidationRun (no longer persisted) ──────────────
        // ConsolidationRuns writes stopped (issue #3028). The PipelineRun is the authoritative
        // record; it is created by PipelineRunFactory.CreateFromWorkItem at dispatch time.
        // The ConsolidationRun object is still built here for its RunId (needed for
        // workspace path) and trace context, but it is NOT written to the store.
        var traceContext = PipelineTelemetry.CaptureTraceContext("TriggerConsolidation");
        var run = BuildNewRun(type, templateIdValue, templateName, projectName, projectId, autoDispatch, _config);
        run.TraceParent = traceContext?.GetValueOrDefault("traceparent");

        // ── 5. Prepare feedback data (harness suggestions path) ───────────────
        if (type == ConsolidationRunType.HarnessSuggestions)
            await _feedbackCache.PrepareFeedbackDataAsync(run, ct);

        // ── 7. Build and submit the JobDistributionRequest ───────────────────
        // IssueIdentifier format: "{type}:{templateId|global}" (issue #3027).
        // This deterministic format feeds the partial unique index on
        // (IssueIdentifier, IssueProviderConfigId) for non-terminal statuses in
        // PipelineDbContext.OnModelCreating, providing cross-replica dedup.
        var issueIdentifier = templateIdValue is not null
            ? $"{type}:{templateIdValue}"
            : $"{type}:global";

        var request = new JobDistributionRequest
        {
            IssueIdentifier = issueIdentifier,
            IssueProviderConfigId = ConsolidationConstants.ProviderConfigId,
            RepoProviderConfigId = repoProviderId,
            BrainProviderConfigId = brainProviderId,
            InitiatedBy = ConsolidationConstants.InitiatedBy,
            TaskType = WorkItemTaskType.Consolidation,
            AgentSelector = AgentSelectorKey.From(selectorLabels),
            TimeoutSeconds = (int)_config.AgentTimeout.TotalSeconds,
            ConsolidationRunType = type,
            ConsolidationTemplateId = templateIdValue,
            // Use run.RunId (not a fresh Guid) so the workspace path is consistent with what
            // CleanupWorkspaceIfSucceeded targets: UpdateRunAsync → CleanupWorkspaceIfSucceeded(runId)
            // computes GetWorkspacePath(run.RunId). A mismatch causes the real workspace directory to
            // never be deleted on completion, leaking one directory per successful run.
            ConsolidationWorkspacePath = _workspaceManager.GetWorkspacePath(run.RunId),
            AutoDispatch = autoDispatch,
            ProjectId = !string.IsNullOrEmpty(projectId) && Guid.TryParse(projectId, out var pid)
                ? pid
                : (Guid?)null,
            ProjectName = projectName,
            // TraceContext captured before dispatch so the resulting WorkItem inherits
            // the originating trace even when dispatched through the API asynchronously.
            TraceContext = traceContext
        };

        DistributionResult result;
        try
        {
            result = await _workDistributor.DistributeAsync(request, ct);
        }
        catch (Exception ex)
        {
            _logger.Error(ex,
                "ConsolidationService: unexpected error calling DistributeAsync for {Type}/{TemplateId}",
                type, templateIdValue ?? "Global");
            _feedbackCache.ClearFeedbackDataForRun(run.RunId);
            return null;
        }

        if (!result.Success)
        {
            if (result.IsPermanentFailure)
            {
                // Permanent failure (e.g. no job template for the resolved selector).
                PipelineTelemetry.ConsolidationDispatchPermanentFailures.Add(1,
                    new KeyValuePair<string, object?>("run.type", type.ToString()));
                _logger.Error(
                    "ConsolidationService: permanent dispatch failure for {Type}/{TemplateId}: {Error}. " +
                    "No WorkItem created. Re-trigger after fixing the agent configuration.",
                    type, templateIdValue ?? "Global", result.ErrorMessage);
            }
            else
            {
                // Transient failure (capacity limit, PVC unavailable, etc.).
                _logger.Warning(
                    "ConsolidationService: transient dispatch failure for {Type}/{TemplateId}: {Error}. " +
                    "Re-trigger to retry.",
                    type, templateIdValue ?? "Global", result.ErrorMessage);
            }
            _feedbackCache.ClearFeedbackDataForRun(run.RunId);
            return null;
        }

        // ── 8. Detect duplicate rejection from the API layer ─────────────────
        // KubernetesWorkDistributor maps a 409 Conflict from POST /api/work-items to
        // DistributionResult(Success: true, WorkItemId: null, Queued: true). This happens
        // when the partial unique index on (IssueIdentifier, IssueProviderConfigId) rejects
        // a duplicate insert because a live WorkItem already exists for this consolidation type.
        // TODO [WARNING]: The null-WorkItemId sentinel is an implicit coupling to KubernetesWorkDistributor's
        // internal 409-handling convention. DistributionResult.WorkItemId is documented as "null if not
        // applicable", so any future or alternate IWorkDistributor that returns Success=true, WorkItemId=null
        // for a legitimate non-duplicate enqueue would be silently misclassified as a duplicate here.
        // Consider adding an explicit AlreadyExists/Duplicate flag to DistributionResult.
        if (result.WorkItemId is null)
        {
            _logger.Warning(
                "ConsolidationService: duplicate rejected for {Type}/{TemplateId} — " +
                "a live WorkItem already exists (API returned 409).",
                type, templateIdValue ?? "Global");
            _feedbackCache.ClearFeedbackDataForRun(run.RunId);
            return null;
        }

        // ── 9. Record WorkItemId on the in-memory run object ─────────────────
        // The WorkItemId is stored on the run for workspace path consistency. ConsolidationRun
        // store writes have been stopped (issue #3028); the PipelineRun is the authoritative record.
        run.WorkItemId = result.WorkItemId;

        _logger.Information("Consolidation run {RunId} created: {Type} for {TemplateName} (WorkItem {WorkItemId} created as Pending)",
            run.RunId, type, templateName, result.WorkItemId);
        OnChange?.Invoke();
        return run;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ConsolidationRun>> GetRunHistoryAsync(CancellationToken ct)
    {
        // Always read from store — the store is the authoritative source.
        // In the multi-process architecture (Spec 041+), the API updates ConsolidationRun
        // status directly in the DB. An in-memory cache here would lag behind those updates
        // and cause the monitoring page to show stale Pending status after dispatch.
        var runs = await _runStore.LoadAllRunsAsync(ct);
        return runs.OrderByDescending(r => r.StartedAtUtc).ToList();
    }

    /// <inheritdoc />
    public async Task<ConsolidationRun?> GetLastRunAsync(
        ConsolidationRunType type, TemplateId? templateId, CancellationToken ct)
    {
        var templateIdValue = templateId?.Value;
        var allRuns = await GetRunHistoryAsync(ct);
        return allRuns
            .Where(r => r.Type == type && r.TemplateId == templateIdValue)
            .OrderByDescending(r => r.StartedAtUtc)
            .FirstOrDefault();
    }

    /// <inheritdoc />
    public async Task UpdateRunAsync(
        RunId runId, ConsolidationRunStatus status, string? summary,
        CancellationToken ct, long totalTokens = 0)
    {
        if (!Guid.TryParse(runId.Value, out _))
        {
            _logger.Warning("Invalid runId format: {RunId}", LogSanitizer.SanitizeForLog(runId.Value));
            return;
        }

        // ConsolidationRuns writes have been stopped (issue #3028). The PipelineRun row is the
        // authoritative terminal-state record (written by RunLifecycleManager).
        // Preserve workspace cleanup as a side effect since ConsolidationRunEndpoints.TransitionStatus
        // still calls this method during the sub-issue #7–#9 transition period.
        _workspaceManager.CleanupWorkspaceIfSucceeded(runId, status);

        _logger.Information("Consolidation run {RunId} UpdateRunAsync: workspace cleanup only (store writes removed, issue #3028)", runId.Value);
        // OnChange is intentionally NOT fired here — ConsolidationService.OnChange is being replaced
        // with IAgentHubConnection hub event subscription in Consolidation.razor (issue #3028).
    }

    /// <inheritdoc />
    public async Task TransitionToRunningAsync(RunId runId, CancellationToken ct)
    {
        if (!Guid.TryParse(runId.Value, out _))
            return;

        // ConsolidationRuns writes stopped (issue #3028). PipelineRun is the authoritative record.
        // Remaining callers are handled by the lifecycle manager (CompleteRunAsync / FailRunAsync).
        _logger.Information("Consolidation run {RunId} TransitionToRunningAsync: store writes removed (issue #3028)", runId.Value);
        // OnChange intentionally not fired (see UpdateRunAsync comment above).
        await Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task<HarnessSuggestions?> GetHarnessSuggestionsAsync(CancellationToken ct)
    {
        try { return await _harnessSuggestionStore.LoadAsync(ct); }
        catch (Exception ex) { _logger.Warning(ex, "Failed to read harness suggestions"); return null; }
    }

    /// <inheritdoc />
    public async Task SaveHarnessSuggestionsAsync(HarnessSuggestions suggestions, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(suggestions);
        try
        {
            await _harnessSuggestionStore.SaveAsync(suggestions, ct);
            _logger.Information("Harness suggestions saved");
            OnChange?.Invoke();
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Failed to save harness suggestions");
        }
    }

    /// <summary>Clears in-memory concurrency state. Used by E2E tests for isolation.</summary>
    /// <remarks>
    /// _runningRuns was removed in issue #3027 — dedup is now fully DB-layer via the partial
    /// unique index on (IssueIdentifier, IssueProviderConfigId). This method is kept as a
    /// no-op to avoid breaking call sites in E2E infrastructure until they are updated.
    /// </remarks>
    internal void Reset() { /* no-op: _runningRuns removed in issue #3027 */ }

    // TODO [WARNING]: PersistRunAsync and RollbackRunAsync below are dead code — no callers remain
    // in ConsolidationService after issue #3028 removed all TriggerAsync store writes. They are a
    // latent regression risk: a future edit could re-invoke them and silently reintroduce
    // ConsolidationRuns writes. Consider removing these methods in a follow-up cleanup PR.
    // (review-findings correctness)
    private async Task PersistRunAsync(ConsolidationRun run, CancellationToken ct)
    {
        await _runStore.SaveRunAsync(run, ct);
    }

    /// <inheritdoc />
    public async Task DeleteRunAsync(RunId runId, CancellationToken ct)
    {
        try
        {
            await _runStore.DeleteRunAsync(runId, ct);
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Failed to delete consolidation run {RunId}", runId.Value);
        }
    }

    /// <summary>
    /// Rolls back a run that failed to persist or whose dispatch was rejected.
    /// Deletes the persisted record and clears any cached feedback data.
    /// Safe to call even when the run was never persisted.
    /// </summary>
    // TODO [WARNING]: RollbackRunAsync is dead code — no callers remain after issue #3028.
    // See PersistRunAsync above. Remove in a follow-up cleanup PR. (review-findings correctness)
    private async Task RollbackRunAsync(string runId)
    {
        await DeletePersistedRunAsync(runId);
        _feedbackCache.ClearFeedbackDataForRun(runId);
    }

    /// <summary>Deletes a persisted run (used when persist fails and the run must be rolled back).</summary>
    internal async Task DeletePersistedRunAsync(string runId)
    {
        ArgumentNullException.ThrowIfNull(runId);
        try
        {
            await _runStore.DeleteRunAsync(runId, CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Failed to delete persisted consolidation run {RunId}", runId);
        }
    }

    private static bool IsTerminalStatus(ConsolidationRunStatus status) =>
        status is ConsolidationRunStatus.Succeeded
            or ConsolidationRunStatus.Failed
            or ConsolidationRunStatus.Cancelled;

    private static ConsolidationRun BuildNewRun(
        ConsolidationRunType type,
        string? templateIdValue,
        string templateName,
        string? projectName,
        string? projectId,
        bool autoDispatch,
        PipelineConfiguration config) => new()
        {
            RunId = Guid.NewGuid().ToString(),
            Type = type,
            TemplateId = templateIdValue,
            TemplateName = templateName,
            StartedAtUtc = DateTimeOffset.UtcNow,
            // New runs start as Pending — the WorkItem has been successfully submitted to the
            // unified dispatch queue. The Scheduler's WorkItemDispatchLoop will create the K8s Job.
            Status = ConsolidationRunStatus.Pending,
            AutoDispatch = autoDispatch,
            ProjectName = projectName,
            ProjectId = projectId,
            // TraceParent is populated after BuildNewRun returns (set from the request TraceContext).
        };
}
