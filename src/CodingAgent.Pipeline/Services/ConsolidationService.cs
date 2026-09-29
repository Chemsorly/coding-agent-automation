using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;
using CodingAgent.Pipeline.Telemetry;
using Serilog;

namespace CodingAgent.Pipeline.Services;

/// <summary>
/// Manages consolidation loop execution: triggering runs, tracking history,
/// persisting run records, and managing harness suggestions.
///
/// <para>
/// After issue #3028: <c>TriggerAsync</c> no longer persists to the <c>IConsolidationRunStore</c>.
/// It dispatches the WorkItem first via <c>IWorkDistributor</c> and returns a <c>ConsolidationRun</c>
/// object built from memory. The <c>PipelineRun</c> (in the work-item database) is the authoritative
/// record. <c>UpdateRunAsync</c>, <c>CleanupOrphanedRunsAsync</c>, and <c>TransitionToRunningAsync</c>
/// no longer write back to the <c>IConsolidationRunStore</c>.
/// </para>
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
    private readonly IProjectStore _projectStore;
    private readonly IWorkDistributor? _workDistributor;
    private readonly IConsolidationSelectorResolver? _selectorResolver;

    /// <inheritdoc />
    public event Action? OnChange;

    /// <inheritdoc />
    /// <remarks>
    /// After issue #3028: TriggerAsync no longer writes to the ConsolidationRuns store.
    /// IsRunActive queries the store, so it will always return false for runs created after #3028
    /// (since they are not persisted there). The PipelineRun / WorkItem is the authoritative record.
    /// </remarks>
    // TODO [WARNING]: Sync-over-async via .GetAwaiter().GetResult(). If IConsolidationService
    // is called from a Blazor Server circuit thread (which has a SynchronizationContext), this will
    // deadlock. Additionally, after issue #3028, this method always returns false for any run created
    // via TriggerAsync (those runs are not written to the ConsolidationRuns store). Callers that rely
    // on this method to detect active runs will silently see no active runs. Prefer converting the
    // interface to Task<bool> IsRunActiveAsync(RunId, CancellationToken) to eliminate both risks.
    public bool IsRunActive(RunId runId)
    {
        var run = _runStore.GetByIdAsync(runId, CancellationToken.None).GetAwaiter().GetResult();
        return run is not null && !IsTerminalStatus(run.Status);
    }

    /// <inheritdoc />
    /// <remarks>
    /// After issue #3028: TriggerAsync no longer writes to the ConsolidationRuns store.
    /// GetActiveRunStartedAt queries the store, so it will return null for runs created after #3028.
    /// </remarks>
    // TODO [WARNING]: Sync-over-async via .GetAwaiter().GetResult(). Same deadlock risk as
    // IsRunActive above. Additionally, after issue #3028, this method always returns null for any
    // run created via TriggerAsync. The XML doc states this is "Used by ReconciliationService
    // (JobController) to detect stuck consolidation runs" — that caller will silently stop detecting
    // stuck runs for all consolidation runs created after #3028. Prefer an async interface method
    // and update ReconciliationService to await it.
    public DateTimeOffset? GetActiveRunStartedAt(RunId runId)
    {
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
        // Cancelled) must never be overwritten. The store is no longer the authoritative record
        // after issue #3028, so we only log these rather than writing back.
        foreach (var run in allRuns.Where(r => r.Status == ConsolidationRunStatus.Running))
        {
            // Skip runs where an agent is still actively working.
            if (activeAgentJobIds.Contains(run.RunId))
            {
                _logger.Information("Consolidation run {RunId} ({Type}) has active agent — skipping orphan cleanup", run.RunId, run.Type);
                continue;
            }

            // Issue #3028: no store writes. Mutate in-memory only for log output.
            run.Status = ConsolidationRunStatus.Failed;
            run.Summary = "Orphaned: application restarted before completion";
            run.CompletedAtUtc = DateTimeOffset.UtcNow;
            // No SaveRunAsync — the ConsolidationRuns store is no longer authoritative.
            _logger.Information("Consolidation run {RunId} ({Type}) appears orphaned — not writing to store (issue #3028)", run.RunId, run.Type);
        }

        // _runningRuns removed (issue #3027): the DB-layer partial unique index on
        // (IssueIdentifier, IssueProviderConfigId) is now the authoritative dedup guard.
        // Pending runs no longer need to be rehydrated into an in-memory dictionary.
        foreach (var run in allRuns.Where(r => r.Status == ConsolidationRunStatus.Pending))
        {
            _logger.Information(
                "Consolidation run {RunId} ({Type}) is Pending from previous session — WorkItem already in DB queue (not re-dispatched)",
                run.RunId, run.Type);
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// After issue #3028: TriggerAsync dispatches via IWorkDistributor first, then builds and
    /// returns a ConsolidationRun object from memory. No writes to IConsolidationRunStore occur.
    /// The PipelineRun (WorkItem record) is the authoritative record.
    /// </remarks>
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
            selectorLabels = LabelResolver.ResolveRequiredLabels(repoConfig, _config);
        }

        // ── 4. Prepare feedback data (harness suggestions path) ───────────────
        // Build a temporary run object to pass to PrepareFeedbackDataAsync (which needs RunId).
        // This run is NOT persisted to the store.
        var traceContext = PipelineTelemetry.CaptureTraceContext("TriggerConsolidation");
        var run = BuildNewRun(type, templateIdValue, templateName, projectName, projectId, autoDispatch, _config);
        run.TraceParent = traceContext?.GetValueOrDefault("traceparent");

        if (type == ConsolidationRunType.HarnessSuggestions)
            await _feedbackCache.PrepareFeedbackDataAsync(run, ct);

        // ── 5. Build and submit the JobDistributionRequest ───────────────────
        // Issue #3028: dispatch FIRST, no persist-before-dispatch. The ConsolidationRun
        // object returned to the caller is built from memory only.
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
            // CleanupWorkspaceIfSucceeded targets.
            ConsolidationWorkspacePath = _workspaceManager.GetWorkspacePath(run.RunId),
            AutoDispatch = autoDispatch,
            ProjectId = !string.IsNullOrEmpty(projectId) && Guid.TryParse(projectId, out var pid)
                ? pid
                : (Guid?)null,
            ProjectName = projectName,
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
                PipelineTelemetry.ConsolidationDispatchPermanentFailures.Add(1,
                    new KeyValuePair<string, object?>("run.type", type.ToString()));
                _logger.Error(
                    "ConsolidationService: permanent dispatch failure for {Type}/{TemplateId}: {Error}. " +
                    "No WorkItem created. Re-trigger after fixing the agent configuration.",
                    type, templateIdValue ?? "Global", result.ErrorMessage);
            }
            else
            {
                _logger.Warning(
                    "ConsolidationService: transient dispatch failure for {Type}/{TemplateId}: {Error}. " +
                    "Re-trigger to retry.",
                    type, templateIdValue ?? "Global", result.ErrorMessage);
            }
            _feedbackCache.ClearFeedbackDataForRun(run.RunId);
            return null;
        }

        // ── 6. Detect duplicate rejection from the API layer ─────────────────
        // KubernetesWorkDistributor maps a 409 Conflict from POST /api/work-items to
        // DistributionResult(Success: true, WorkItemId: null, Queued: true). This signals that
        // a live WorkItem already exists for this consolidation type (partial unique index).
        if (result.WorkItemId is null)
        {
            _logger.Warning(
                "ConsolidationService: duplicate rejected for {Type}/{TemplateId} — " +
                "a live WorkItem already exists (API returned 409).",
                type, templateIdValue ?? "Global");
            _feedbackCache.ClearFeedbackDataForRun(run.RunId);
            return null;
        }

        // ── 7. Populate WorkItemId on the run object and return ───────────────
        // Issue #3028: no store writes. The run is returned as an in-memory object only.
        run.WorkItemId = result.WorkItemId;

        _logger.Information("Consolidation run {RunId} created: {Type} for {TemplateName} (WorkItem {WorkItemId} created as Pending)",
            run.RunId, type, templateName, result.WorkItemId);
        OnChange?.Invoke();
        return run;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ConsolidationRun>> GetRunHistoryAsync(CancellationToken ct)
    {
        // Always read from store — the store is the authoritative source for historical records.
        // Note: after issue #3028, TriggerAsync no longer writes to this store, so only runs
        // seeded directly (e.g. by legacy paths or tests) will appear here.
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
    /// <remarks>
    /// After issue #3028: this method is a no-op for store writes. It only performs workspace
    /// cleanup as a side effect when a terminal status is given and the runId is a valid GUID
    /// (workspace paths are based on RunId GUIDs, not WorkItemIds). The PipelineRun / WorkItem
    /// is the authoritative record for run status.
    /// </remarks>
    // TODO [WARNING]: This method was converted from async Task to non-async Task returning
    // Task.CompletedTask. The call to _workspaceManager.CleanupWorkspaceIfSucceeded is currently
    // void and synchronous (ConsolidationWorkspaceManager.cs:47), so this is safe. If that method
    // is ever made async (returns Task), the current body would fire-and-forget the Task and
    // silently discard any exceptions from workspace cleanup. Verify the signature of
    // CleanupWorkspaceIfSucceeded before changing it, and update this method to await it.
    public Task UpdateRunAsync(
        RunId runId,
        ConsolidationRunStatus status,
        string? summary,
        CancellationToken ct,
        long totalTokens = 0)
    {
        if (string.IsNullOrWhiteSpace(runId.Value))
        {
            _logger.Warning("UpdateRunAsync: invalid runId — null or empty (no-op after #3028)");
            return Task.CompletedTask;
        }

        // Issue #3028: store writes removed. Only perform workspace cleanup side-effect
        // when the runId is a valid GUID (workspace paths use RunId, not WorkItemId).
        // The hub may call this with a WorkItemId (not a GUID) — skip workspace cleanup
        // in that case to avoid ArgumentException from ConsolidationWorkspaceManager.
        if (Guid.TryParse(runId.Value, out _))
        {
            _workspaceManager.CleanupWorkspaceIfSucceeded(runId, status);
        }

        _logger.Debug("UpdateRunAsync: no-op for store (issue #3028) — runId={RunId} status={Status}", runId.Value, status);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    /// <remarks>
    /// After issue #3028: this method is a no-op. The PipelineRun / WorkItem is the authoritative
    /// record; no ConsolidationRun record is maintained by TriggerAsync.
    /// </remarks>
    public Task TransitionToRunningAsync(RunId runId, CancellationToken ct)
    {
        // Issue #3028: no store writes. This method is a no-op.
        _logger.Debug("TransitionToRunningAsync: no-op for store (issue #3028) — runId={RunId}", runId.Value);
        return Task.CompletedTask;
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

    // TODO [WARNING]: PersistRunAsync and DeletePersistedRunAsync below have no callers in
    // ConsolidationService after issue #3028 removed all TriggerAsync store writes. These are dead
    // code and a latent regression risk: a future edit could re-invoke PersistRunAsync and silently
    // reintroduce ConsolidationRuns store writes that the #3028 design decision explicitly eliminated.
    // Consider removing these methods in a follow-up cleanup PR once it is confirmed no external
    // callers remain (check ConsolidationRunEndpoints and test infrastructure).
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

    /// <summary>Deletes a persisted run. Safe to call even when the run was never persisted.</summary>
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
        };
}
