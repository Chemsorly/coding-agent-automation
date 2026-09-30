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
    private readonly IConsolidationFeedbackCache _feedbackCache;
    private readonly ConsolidationTemplateResolver _templateResolver;
    private readonly IProviderConfigStore _providerConfigStore;
    private readonly IProjectStore _projectStore;
    private readonly IWorkDistributor? _workDistributor;
    private readonly IConsolidationSelectorResolver? _selectorResolver;
    private readonly IPipelineConfigStore? _pipelineConfigStore;

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
        _feedbackCache = deps.FeedbackCache ?? new ConsolidationFeedbackCache(deps.Logger, deps.RunStore, deps.RunHistoryService);
        _templateResolver = new ConsolidationTemplateResolver(deps.ProjectStore);
        _providerConfigStore = deps.ProviderConfigStore;
        _projectStore = deps.ProjectStore;
        _workDistributor = deps.WorkDistributor;
        _selectorResolver = deps.SelectorResolver;
        _pipelineConfigStore = deps.PipelineConfigStore;
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

        // ── 0. Load live configuration ────────────────────────────────────────
        // Prefer the live config from the store so that operator-updated settings
        // (e.g. timeout, BrainReadOnly, DefaultRequiredAgentLabels) are used immediately
        // without a service restart. Fall back to the bootstrap config in tests and
        // legacy call sites that do not inject IPipelineConfigStore.
        var liveConfig = _pipelineConfigStore is not null
            ? await _pipelineConfigStore.LoadPipelineConfigAsync(ct)
            : _config;

        // ── 1. Resolve template + project ────────────────────────────────────
        string? templateName;
        string? projectName = null;
        string? projectId = null;
        PipelineProject? project = null;
        ProviderConfig? repoConfig = null;
        string repoProviderId = "";
        string? brainProviderId = null;
        PipelineJobTemplate? template = null;

        if (templateId is not null)
        {
            var (resolvedTemplate, resolvedProject) = await _templateResolver.ResolveTemplateAndProjectAsync(templateIdValue!, ct);
            if (resolvedTemplate is null)
            {
                _logger.Warning("Consolidation run rejected: template {TemplateId} not found", templateIdValue);
                return null;
            }
            template = resolvedTemplate;
            project = resolvedProject;
            templateName = template.Name;
            projectName = project?.Name;
            projectId = project?.Id;

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

        // ── 1b. Reject brain consolidation for read-only brains ───────────────
        // Brain consolidation writes to the brain — it must not run if the brain is read-only.
        // The template flag can only make a brain read-only. The global config with the project's
        // override decides when the template flag is false.
        if (type == ConsolidationRunType.BrainConsolidation && template is not null
            && ConsolidationTemplateFilter.IsBrainReadOnly(template, project, liveConfig))
        {
            _logger.Warning(
                "Consolidation run rejected: brain is read-only for template {TemplateId}",
                templateIdValue);
            return null;
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
            // Use the live config so that operator-updated DefaultRequiredAgentLabels are applied.
            selectorLabels = await _selectorResolver.ResolveAsync(repoConfig, liveConfig, ct);
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
            selectorLabels = LabelResolver.ResolveRequiredLabels(repoConfig, liveConfig);
        }

        // ── 4. Prepare feedback data (harness suggestions path) ───────────────
        // Build a temporary run object to pass to PrepareFeedbackDataAsync (which needs RunId).
        // This run is NOT persisted to the store.
        var traceContext = PipelineTelemetry.CaptureTraceContext("TriggerConsolidation");
        // TODO [WARNING]: `_config` (the bootstrap PipelineConfiguration injected at startup) is
        // passed to BuildNewRun instead of `liveConfig` (the live-loaded config resolved at step 0).
        // The `config` parameter is not currently read inside BuildNewRun's object initializer, so
        // there is no immediate regression. However, if a future field on ConsolidationRun is seeded
        // from `config`, the wrong (startup-time) value will be used. Pass `liveConfig` here to
        // ensure any future config-derived field reflects the operator-current settings.
        var run = BuildNewRun(type, templateIdValue, templateName, projectName, projectId, autoDispatch, _config);
        run.TraceParent = traceContext?.GetValueOrDefault("traceparent");

        if (type == ConsolidationRunType.HarnessSuggestions)
            await _feedbackCache.PrepareFeedbackDataAsync(run, ct);

        // ── 4b. Resolve effective timeout ────────────────────────────────────
        // Brain consolidation works per brain, so use the project-level timeout override
        // when the run is template-scoped. Harness suggestions are global so they always
        // use the global timeout. ApplyProjectOverrides handles the null-project case.
        var effectiveConfig = project is not null && templateId is not null
            ? PipelineConfigurationResolver.ApplyProjectOverrides(liveConfig, project)
            : liveConfig;
        var timeoutSeconds = (int)effectiveConfig.AgentTimeout.TotalSeconds;

        // ── 5. Build and submit the JobDistributionRequest ───────────────────
        // Issue #3028: dispatch FIRST, no persist-before-dispatch. The ConsolidationRun
        // object returned to the caller is built from memory only.
        //
        // IssueIdentifier format (issue #3027, updated for brain scope):
        //   - BrainConsolidation: "{type}:{brainProviderId}" — one per brain, regardless of template.
        //     Multiple templates that share a brain use the same dedup key so only one runs at a time.
        //   - Other types: "{type}:{templateId|global}" — one per template.
        // This deterministic format feeds the partial unique index on
        // (IssueIdentifier, IssueProviderConfigId) for non-terminal statuses in
        // PipelineDbContext.OnModelCreating, providing cross-replica dedup.
        var issueIdentifier = type == ConsolidationRunType.BrainConsolidation && !string.IsNullOrEmpty(brainProviderId)
            ? $"{type}:{brainProviderId}"
            : templateIdValue is not null
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
            TimeoutSeconds = timeoutSeconds,
            ConsolidationRunType = type,
            ConsolidationTemplateId = templateIdValue,
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
        // TODO [WARNING]: `result.WorkItemId is null` (with result.Success == true) is used as
        // the sole sentinel for the 409/duplicate path. This overloads a single value for two
        // distinct outcomes: "duplicate rejected" and "any other non-error success with no ID".
        // If any IWorkDistributor implementation ever returns Success=true, WorkItemId=null for a
        // reason other than a 409 (e.g. an implementation that queues without returning an ID),
        // this path silently treats that as a duplicate and returns null, masking the real outcome.
        // The previous code used an explicit result.AlreadyExists flag for disambiguation.
        // Verify that all IWorkDistributor implementations only return null WorkItemId on the 409
        // path before removing this comment. (review-findings-correctness.md WARNING:L308)
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
    /// After issue #3028: this method is a no-op for store writes. The PipelineRun / WorkItem
    /// is the authoritative record for run status.
    /// </remarks>
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

        // TODO [WARNING]: `runId.Value` is written to the log without sanitization. The prior code
        // used `LogSanitizer.SanitizeForLog(runId.Value)` on this path. `runId.Value` originates
        // from the hub completion path where the agent supplies the JobId; a malicious or
        // malfunctioning agent could inject log-format tokens or newlines. Sinks that emit
        // plain-text (console, file) render the raw string. Wrap with
        // `LogSanitizer.SanitizeForLog(runId.Value)` to match the rest of the file.
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
        // TODO [WARNING]: Same log-sanitization regression as UpdateRunAsync above. `runId.Value`
        // comes from the agent-supplied JobId. Wrap with `LogSanitizer.SanitizeForLog(runId.Value)`
        // to prevent log injection in plain-text sinks.
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
