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

            run.Status = ConsolidationRunStatus.Failed;
            run.Summary = "Orphaned: application restarted before completion";
            run.CompletedAtUtc = DateTimeOffset.UtcNow;
            await _runStore.SaveRunAsync(run, ct);
            _logger.Information("Marked orphaned consolidation run {RunId} ({Type}) as Failed", run.RunId, run.Type);
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

        // ── 4. Build the ConsolidationRun and persist it BEFORE dispatch ─────
        // Persist-before-dispatch restores the old safe ordering: if DistributeAsync fails,
        // the run row already exists and can be rolled back cleanly. Reversing this (dispatch
        // then persist) leaves an orphaned Pending WorkItem when PersistRunAsync throws —
        // the Scheduler dispatches it, the agent starts, but no ConsolidationRun row exists.
        // (DotNetSpecialist CRITICAL finding — dispatch-before-persist ordering)
        var traceContext = PipelineTelemetry.CaptureTraceContext("TriggerConsolidation");
        var run = BuildNewRun(type, templateIdValue, templateName, projectName, projectId, autoDispatch, _config);
        run.TraceParent = traceContext?.GetValueOrDefault("traceparent");

        // ── 5. Prepare feedback data (harness suggestions path) ───────────────
        if (type == ConsolidationRunType.HarnessSuggestions)
            await _feedbackCache.PrepareFeedbackDataAsync(run, ct);

        // ── 6. Persist the run row FIRST ──────────────────────────────────────
        try
        {
            await PersistRunAsync(run, ct);
        }
        catch (Exception ex)
        {
            _logger.Error(ex,
                "Failed to persist consolidation run {RunId} for {Type}/{TemplateName} — rolling back",
                run.RunId, type, templateName);
            await RollbackRunAsync(run.RunId);
            return null;
        }

        // ── 7. Build and submit the JobDistributionRequest ───────────────────
        // Dispatch runs after a successful persist so that any dispatch failure can be
        // compensated: the persisted run row is deleted and the dedup key is cleared.
        //
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
            // (Fix for CRITICAL finding in review-findings-correctness.md / review-findings-dotnetspecialist.md)
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
                "ConsolidationService: unexpected error calling DistributeAsync for {Type}/{TemplateId} — rolling back persisted run",
                type, templateIdValue ?? "Global");
            await RollbackRunAsync(run.RunId);
            return null;
        }

        if (!result.Success)
        {
            if (result.IsPermanentFailure)
            {
                // Permanent failure (e.g. no job template for the resolved selector).
                // Increment the telemetry counter (was previously in ConsolidationDispatcher).
                PipelineTelemetry.ConsolidationDispatchPermanentFailures.Add(1,
                    new KeyValuePair<string, object?>("run.type", type.ToString()));
                _logger.Error(
                    "ConsolidationService: permanent dispatch failure for {Type}/{TemplateId}: {Error}. " +
                    "No WorkItem created; rolling back persisted run. Re-trigger after fixing the agent configuration.",
                    type, templateIdValue ?? "Global", result.ErrorMessage);
            }
            else
            {
                // Transient failure (capacity limit, PVC unavailable, etc.).
                // In the synchronous path the caller must re-trigger — no retry sweep exists.
                _logger.Warning(
                    "ConsolidationService: transient dispatch failure for {Type}/{TemplateId}: {Error}. " +
                    "Rolling back persisted run. Re-trigger to retry.",
                    type, templateIdValue ?? "Global", result.ErrorMessage);
            }
            // Neither failure type leaves a run row — roll back the persisted run.
            await RollbackRunAsync(run.RunId);
            return null;
        }

        // ── 8. Detect duplicate rejection from the API layer ─────────────────
        // KubernetesWorkDistributor maps a 409 Conflict from POST /api/work-items to
        // DistributionResult(Success: true, WorkItemId: null, Queued: true). This happens
        // when the partial unique index on (IssueIdentifier, IssueProviderConfigId) rejects
        // a duplicate insert because a live WorkItem already exists for this consolidation type.
        // We must detect this here and treat it as "already running" rather than success,
        // because no second WorkItem was created and the persisted ConsolidationRun must be
        // rolled back to avoid an orphaned Pending run in the history.
        // TODO [WARNING]: The null-WorkItemId sentinel is an implicit coupling to KubernetesWorkDistributor's
        // internal 409-handling convention. DistributionResult.WorkItemId is documented as "null if not
        // applicable", so any future or alternate IWorkDistributor that returns Success=true, WorkItemId=null
        // for a legitimate non-duplicate enqueue would be silently misclassified as a duplicate here,
        // rolling back a valid run and reporting "already running". Consider adding an explicit
        // AlreadyExists/Duplicate flag to DistributionResult so the duplicate-rejection signal is
        // unambiguous and not overloaded on the null-WorkItemId sentinel.
        // (review-findings-correctness.md)
        if (result.WorkItemId is null)
        {
            _logger.Warning(
                "ConsolidationService: duplicate rejected for {Type}/{TemplateId} — " +
                "a live WorkItem already exists (API returned 409). Rolling back persisted run.",
                type, templateIdValue ?? "Global");
            await RollbackRunAsync(run.RunId);
            return null;
        }

        // ── 9. Record WorkItemId and re-persist ──────────────────────────────
        // Store the WorkItem ID on the run so the Consolidation page can cancel via
        // PostStatus(Cancelled) without needing the now-removed CancelQueuedRunAsync.
        run.WorkItemId = result.WorkItemId;
        try
        {
            await PersistRunAsync(run, ct);
        }
        catch (Exception ex)
        {
            // Non-fatal: the run is already Pending, the WorkItem exists, the agent will proceed.
            // Only the WorkItemId field is missing from the persisted record.
            // TODO [WARNING]: When this catch fires, the run is Pending in the store with WorkItemId==null.
            // The Consolidation page's cancel button is gated on WorkItemId being non-null, so the run
            // will be non-cancellable via the UI for its entire non-terminal lifetime — a user-visible
            // loss of the cancel capability that the issue explicitly requires. The current log level
            // (Warning) may not surface this visibly enough for operators. Consider elevating to Error
            // so it is captured by alerting pipelines, and document that operators must cancel the
            // corresponding WorkItem directly if this condition is detected.
            // (review-findings-correctness.md)
            _logger.Warning(ex,
                "ConsolidationService: failed to re-persist WorkItemId for run {RunId} — cancel via UI may not work for this run",
                run.RunId);
        }

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

        try
        {
            var run = await _runStore.GetByIdAsync(runId, ct);
            if (run is null)
            {
                _logger.Warning("Cannot update consolidation run {RunId}: not found", runId.Value);
                return;
            }

            if (IsTerminalStatus(run.Status))
            {
                _logger.Debug(
                    "Skipping update for consolidation run {RunId}: already in terminal status {CurrentStatus} (requested: {RequestedStatus})",
                    runId.Value, run.Status, status);
                return;
            }

            run.Status = status;
            run.Summary = summary;
            if (IsTerminalStatus(status))
                run.CompletedAtUtc = DateTimeOffset.UtcNow;
            run.TotalTokens = totalTokens;

            await PersistRunAsync(run, ct);

            _workspaceManager.CleanupWorkspaceIfSucceeded(runId, status);
            _logger.Information("Consolidation run {RunId} updated: {Status} — {Summary}", runId.Value, status, LogSanitizer.SanitizeForLog(summary ?? "(no summary)"));
            OnChange?.Invoke();
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Failed to update consolidation run {RunId}", runId.Value);
        }
    }

    /// <inheritdoc />
    public async Task TransitionToRunningAsync(RunId runId, CancellationToken ct)
    {
        if (!Guid.TryParse(runId.Value, out _))
            return;

        try
        {
            var run = await _runStore.GetByIdAsync(runId, ct);
            // Accept only Pending (the WorkItem was enqueued and the Scheduler is now starting the K8s Job).
            if (run is null || run.Status != ConsolidationRunStatus.Pending)
                return;

            run.Status = ConsolidationRunStatus.Running;
            run.StartedAtUtc = DateTimeOffset.UtcNow;
            await PersistRunAsync(run, ct);

            _logger.Information("Consolidation run {RunId} transitioned from Pending to Running (StartedAtUtc reset)", runId.Value);
            OnChange?.Invoke();
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Failed to transition consolidation run {RunId} to Running", runId.Value);
        }
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
