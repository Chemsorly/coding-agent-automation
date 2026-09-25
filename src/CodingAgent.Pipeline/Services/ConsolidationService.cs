using System.Collections.Concurrent;
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

    private readonly ConcurrentDictionary<(ConsolidationRunType, string?), ConsolidationRun> _runningRuns = new();

    /// <inheritdoc />
    public event Action? OnChange;

    /// <inheritdoc />
    public bool IsRunActive(RunId runId) => _runningRuns.Values.Any(r => r.RunId == runId.Value);

    /// <inheritdoc />
    public DateTimeOffset? GetActiveRunStartedAt(RunId runId)
    {
        var run = _runningRuns.Values.FirstOrDefault(r => r.RunId == runId.Value);
        return run?.StartedAtUtc;
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
                // Restore to _runningRuns so TriggerAsync dedup guard works correctly and
                // prevents a duplicate dispatch for the same (type, templateId) key.
                var key = (run.Type, run.TemplateId);
                _runningRuns.TryAdd(key, run);
                continue;
            }

            run.Status = ConsolidationRunStatus.Failed;
            run.Summary = "Orphaned: application restarted before completion";
            run.CompletedAtUtc = DateTimeOffset.UtcNow;
            await _runStore.SaveRunAsync(run, ct);
            _logger.Information("Marked orphaned consolidation run {RunId} ({Type}) as Failed", run.RunId, run.Type);
        }

        // Re-add Pending runs to _runningRuns for dedup only — their WorkItem already
        // exists in the DB and will be picked up by the Scheduler. Without this, a restart
        // with a Pending run leaves the (type, templateId) key absent from _runningRuns,
        // so TriggerAsync's TryAdd succeeds and creates a duplicate ConsolidationRun + WorkItem.
        // Mirrors the Pending-run rehydration that was previously in RehydrateQueuedRunsAsync
        // (issue #2619 fix). RehydrateQueuedRunsAsync is removed; startup cleanup is the
        // single entry point for restoring in-memory dedup state.
        foreach (var run in allRuns.Where(r => r.Status == ConsolidationRunStatus.Pending))
        {
            var key = (run.Type, run.TemplateId);
            _runningRuns.TryAdd(key, run);
            _logger.Information(
                "Rehydrated pending consolidation run {RunId} ({Type}) into dedup tracker (not re-dispatched)",
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
        var key = (type, templateIdValue);

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

        // ── 3. Dedup guard — check BEFORE calling the distributor ─────────────
        // This prevents duplicate WorkItems from being submitted to the API when the
        // same (type, templateId) is triggered concurrently or while a run is active.
        // We build a placeholder run for the dedup map; it will be replaced with the
        // real run once dispatch succeeds.
        var placeholder = new ConsolidationRun
        {
            RunId = Guid.NewGuid().ToString(),
            Type = type,
            TemplateId = templateIdValue,
            TemplateName = templateName,
            StartedAtUtc = DateTimeOffset.UtcNow,
            Status = ConsolidationRunStatus.Pending,
            AutoDispatch = autoDispatch,
            ProjectName = projectName,
            ProjectId = projectId
        };

        if (!_runningRuns.TryAdd(key, placeholder))
        {
            // Attempt stale-entry eviction: in multi-process mode the API may have
            // completed the run without notifying us.
            var evicted = await TryEvictAndRetryAsync(key, placeholder, type, templateId, ct);
            if (!evicted)
            {
                _logger.Warning(
                    "Consolidation run rejected: {Type} for template {TemplateId} is already running or pending",
                    type, templateId ?? "Global");
                return null;
            }
        }

        // ── 4. Resolve agent selector labels ─────────────────────────────────
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
                // TODO [WARNING]: TryRemove(key, out _) removes unconditionally regardless of whether
                // the current value is still the placeholder inserted by this call. If a concurrent
                // TriggerAsync call raced between the placeholder TryAdd and this TryRemove and replaced
                // the placeholder with its own value, the TryRemove would evict the legitimate newer entry,
                // silently blocking the next trigger for the same (type, templateId). The same hazard
                // applies to the DistributeAsync exception catch below. Consider using the
                // TryRemove(key, specificValue) overload to only remove the placeholder this call owns.
                // Low risk in practice given the single-orchestrator model, but worth fixing for
                // correctness. (review-findings-securityreviewer.md, review-findings-correctness.md)
                _runningRuns.TryRemove(key, out _); // Remove placeholder
                return null;
            }
        }
        else
        {
            // Fallback when no resolver is injected (tests, or legacy call sites).
            // Use LabelResolver which reads from repoConfig + DefaultRequiredAgentLabels.
            selectorLabels = LabelResolver.ResolveRequiredLabels(repoConfig, _config);
        }

        // ── 5. Build and submit the JobDistributionRequest ───────────────────
        // TODO [WARNING]: IssueIdentifier is set to type.ToString() (e.g. "BrainConsolidation") rather
        // than run.RunId (a GUID). Every triggered run of the same type therefore produces an identical
        // IssueIdentifier value. If any downstream consumer uses IssueIdentifier as a dedup key or for
        // run-to-WorkItem correlation (e.g. to drive TransitionToRunningAsync for the correct run),
        // this will cause cross-run collisions. The old ConsolidationDispatcher used run.RunId here.
        // Deterministic IssueIdentifier is deferred to sub-issue #5 — verify the drain/transition path
        // does not rely on this linkage before merging. (review-findings-correctness.md,
        // review-findings-dotnetspecialist.md, review-findings-securityreviewer.md)
        var request = new JobDistributionRequest
        {
            IssueIdentifier = type.ToString(),
            IssueProviderConfigId = ConsolidationConstants.ProviderConfigId,
            RepoProviderConfigId = repoProviderId,
            BrainProviderConfigId = brainProviderId,
            InitiatedBy = ConsolidationConstants.InitiatedBy,
            TaskType = WorkItemTaskType.Consolidation,
            AgentSelector = AgentSelectorKey.From(selectorLabels),
            TimeoutSeconds = (int)_config.AgentTimeout.TotalSeconds,
            ConsolidationRunType = type,
            ConsolidationTemplateId = templateIdValue,
            ConsolidationWorkspacePath = _workspaceManager.GetWorkspacePath(Guid.NewGuid().ToString()),
            AutoDispatch = autoDispatch,
            ProjectId = !string.IsNullOrEmpty(projectId) && Guid.TryParse(projectId, out var pid)
                ? pid
                : (Guid?)null,
            ProjectName = projectName,
            // TraceContext captured before dispatch so the resulting WorkItem inherits
            // the originating trace even when dispatched through the API asynchronously.
            TraceContext = PipelineTelemetry.CaptureTraceContext("TriggerConsolidation")
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
            // TODO [WARNING]: See note on TryRemove above (startup-race path) — same unconditional
            // removal hazard applies here. (review-findings-securityreviewer.md)
            _runningRuns.TryRemove(key, out _); // Remove placeholder
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
                    "No WorkItem created; re-trigger after fixing the agent configuration.",
                    type, templateIdValue ?? "Global", result.ErrorMessage);
            }
            else
            {
                // Transient failure (capacity limit, PVC unavailable, etc.).
                // In the synchronous path the caller must re-trigger — no retry sweep exists.
                _logger.Warning(
                    "ConsolidationService: transient dispatch failure for {Type}/{TemplateId}: {Error}. " +
                    "Re-trigger to retry.",
                    type, templateIdValue ?? "Global", result.ErrorMessage);
            }
            // Neither failure type persists a run row — remove the placeholder from dedup map.
            _runningRuns.TryRemove(key, out _);
            return null;
        }

        // ── 5. Build the real ConsolidationRun and update the dedup map ───────
        // Replace the placeholder with the real run (same key, updated RunId and TraceParent).
        var run = BuildNewRun(type, templateIdValue, templateName, projectName, projectId, autoDispatch, _config);
        _runningRuns[key] = run; // Overwrite the placeholder with the real run.

        // ── 6. Prepare feedback data (harness suggestions path) ───────────────
        if (type == ConsolidationRunType.HarnessSuggestions)
            await _feedbackCache.PrepareFeedbackDataAsync(run, ct);

        // ── 7. Persist the run ────────────────────────────────────────────────
        run.TraceParent = request.TraceContext?.GetValueOrDefault("traceparent");

        // TODO [WARNING]: DistributeAsync (step 5) runs BEFORE PersistRunAsync. If PersistRunAsync
        // throws here, RollbackRunAsync removes the in-memory dedup entry and calls
        // DeletePersistedRunAsync — but DeletePersistedRunAsync only deletes the ConsolidationRun row
        // (which was never written), not the already-created Pending WorkItem in the API DB. The
        // Scheduler will dispatch that orphaned WorkItem; the agent starts but no ConsolidationRun row
        // exists to transition to Running/Succeeded. The dedup guard is also cleared, so a re-trigger
        // will create a second WorkItem for the same template. This is a new failure mode introduced by
        // reversing the dispatch/persist order relative to the old pattern (old: persist first as
        // Queued, then dispatch). Consider persisting before dispatching, or compensating by cancelling
        // the WorkItem via PostStatus on persist failure (cancel-via-PostStatus is scoped to #5).
        // (review-findings-correctness.md, review-findings-dotnetspecialist.md)
        try
        {
            await PersistRunAsync(run, ct);
        }
        catch (Exception ex)
        {
            _logger.Error(ex,
                "Failed to persist consolidation run {RunId} for {Type}/{TemplateName} — rolling back in-memory state",
                run.RunId, type, templateName);
            await RollbackRunAsync(key, run.RunId);
            return null;
        }

        _logger.Information("Consolidation run {RunId} created: {Type} for {TemplateName} (WorkItem created as Pending)",
            run.RunId, type, templateName);
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
            _logger.Warning("Invalid runId format: {RunId}", runId.Value);
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

            // Evict from in-progress tracker when no longer active.
            if (status != ConsolidationRunStatus.Running && status != ConsolidationRunStatus.Pending)
            {
                var key = (run.Type, run.TemplateId);
                _runningRuns.TryRemove(key, out _);
            }

            _workspaceManager.CleanupWorkspaceIfSucceeded(runId, status);
            _logger.Information("Consolidation run {RunId} updated: {Status} — {Summary}", runId.Value, status, summary ?? "(no summary)");
            OnChange?.Invoke();
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Failed to update consolidation run {RunId}", runId.Value);
        }
    }

    /// <inheritdoc />
    public async Task<bool> CancelQueuedRunAsync(RunId runId, CancellationToken ct)
    {
        if (!Guid.TryParse(runId.Value, out _))
            return false;

        try
        {
            var run = await _runStore.GetByIdAsync(runId, ct);
            if (run is null || run.Status != ConsolidationRunStatus.Pending)
                return false;

            run.Status = ConsolidationRunStatus.Cancelled;
            run.CompletedAtUtc = DateTime.UtcNow;
            run.Summary = "Cancelled by user";

            await PersistRunAsync(run, ct);

            var key = (run.Type, run.TemplateId);
            _runningRuns.TryRemove(key, out _);

            _logger.Information("Consolidation run {RunId} cancelled", runId.Value);
            OnChange?.Invoke();
            return true;
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Failed to cancel consolidation run {RunId}", runId.Value);
            return false;
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

            var key = (run.Type, run.TemplateId);
            _runningRuns.AddOrUpdate(key, run, (_, _) => run);

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
    internal void Reset() => _runningRuns.Clear();

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
    /// Rolls back a run that failed to persist by removing it from the in-memory
    /// concurrency tracker, deleting the persisted record, and clearing any cached feedback data.
    /// Safe to call even when the run was never persisted.
    /// </summary>
    private async Task RollbackRunAsync((ConsolidationRunType, string?) key, string runId)
    {
        _runningRuns.TryRemove(key, out _);
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

    private async Task<bool> TryEvictAndRetryAsync(
        (ConsolidationRunType, string?) key,
        ConsolidationRun newRun,
        ConsolidationRunType type,
        TemplateId? templateId,
        CancellationToken ct)
    {
        if (!_runningRuns.TryGetValue(key, out var existing))
            return false;

        var stored = await _runStore.GetByIdAsync(new RunId(existing.RunId), ct);
        if (stored is null || !IsTerminalStatus(stored.Status))
            return false;

        _logger.Information(
            "ConsolidationService: evicting stale _runningRuns entry for {Type}/{TemplateId} " +
            "(store status={Status}) to allow new run",
            type, templateId ?? "Global", stored.Status);
        _runningRuns.TryRemove(key, out _);

        // Retry the add — if it fails again, a genuinely concurrent trigger won
        return _runningRuns.TryAdd(key, newRun);
    }
}
