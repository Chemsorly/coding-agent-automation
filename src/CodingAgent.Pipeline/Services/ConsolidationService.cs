using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;
using CodingAgent.Pipeline.Telemetry;
using Serilog;

namespace CodingAgent.Pipeline.Services;

/// <summary>
/// Manages consolidation loop execution: triggering runs and persisting harness suggestions.
/// </summary>
public sealed class ConsolidationService : IConsolidationService
{
    private readonly ILogger _logger;
    private readonly PipelineConfiguration _config;
    private readonly IConsolidationRunStore _runStore;
    private readonly IHarnessSuggestionStore _harnessSuggestionStore;
    private readonly ConsolidationTemplateResolver _templateResolver;
    private readonly IProviderConfigStore _providerConfigStore;
    private readonly IWorkDistributor? _workDistributor;
    private readonly IConsolidationSelectorResolver? _selectorResolver;
    private readonly IPipelineConfigStore? _pipelineConfigStore;

    /// <inheritdoc />
    public event Action? OnChange;

    public ConsolidationService(
        ConsolidationServiceDependencies deps)
    {
        ArgumentNullException.ThrowIfNull(deps);
        ArgumentNullException.ThrowIfNull(deps.Logger);
        ArgumentNullException.ThrowIfNull(deps.Config);
        ArgumentNullException.ThrowIfNull(deps.ProjectStore);
        ArgumentNullException.ThrowIfNull(deps.RunStore);
        ArgumentNullException.ThrowIfNull(deps.HarnessSuggestionStore);

        _logger = deps.Logger;
        _config = deps.Config;
        _runStore = deps.RunStore;
        _harnessSuggestionStore = deps.HarnessSuggestionStore;
        _templateResolver = new ConsolidationTemplateResolver(deps.ProjectStore);
        _providerConfigStore = deps.ProviderConfigStore;
        _workDistributor = deps.WorkDistributor;
        _selectorResolver = deps.SelectorResolver;
        _pipelineConfigStore = deps.PipelineConfigStore;
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
        PipelineJobTemplate? template = null;
        PipelineProject? project = null;
        ProviderConfig? repoConfig = null;

        if (templateId is not null)
        {
            (template, project) = await _templateResolver.ResolveTemplateAndProjectAsync(templateIdValue!, ct);
            if (template is null)
            {
                _logger.Warning("Consolidation run rejected: template {TemplateId} not found", templateIdValue);
                return null;
            }

            // Resolve repo ProviderConfig (for selector resolution).
            repoConfig = await _providerConfigStore.GetProviderConfigByIdAsync(
                template.RepoProviderId, ProviderKind.Repository, ct);
        }

        var templateName = template?.Name ?? "Global";
        var projectName = project?.Name;
        var projectId = project?.Id;

        // Resolve repoProviderId and brainProviderId for the JobDistributionRequest payload.
        // AgentTokenRefreshService reads RepoProviderConfigId directly from WorkItems.Payload
        // (JSONB) and will fail with HubException if it is empty for template-scoped runs.
        var repoProviderId = template?.RepoProviderId ?? "";
        var brainProviderId = template?.BrainProviderId;

        // The configuration the job runs with: the live global settings with the project's overrides,
        // as the agent resolves them at claim time. The bootstrap _config is only a fallback for hosts
        // without a configuration store (tests); it never reflects saved settings.
        var globalConfig = _pipelineConfigStore is not null
            ? await _pipelineConfigStore.LoadPipelineConfigAsync(ct)
            : _config;
        var config = PipelineConfigurationResolver.ApplyProjectOverrides(globalConfig, project);

        // Brain consolidation writes to the brain, so it does not run from a template whose brain is read-only.
        if (type == ConsolidationRunType.BrainConsolidation && template is not null
            && ConsolidationTemplateFilter.IsBrainReadOnly(template, project, globalConfig))
        {
            _logger.Warning(
                "Consolidation run rejected: the brain of template {TemplateName} is read-only, so brain consolidation does not run from it",
                templateName);
            return null;
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
            selectorLabels = await _selectorResolver.ResolveAsync(repoConfig, config, ct);
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
            selectorLabels = LabelResolver.ResolveRequiredLabels(repoConfig, config);
        }

        // ── 4. Build the ConsolidationRun (no longer persisted) ──────────────
        // ConsolidationRuns writes stopped (issue #3028). The PipelineRun is the authoritative
        // record; it is created by PipelineRunFactory.CreateFromWorkItem at dispatch time.
        // The ConsolidationRun object is still built here for its RunId and trace context,
        // but it is NOT written to the store.
        var traceContext = PipelineTelemetry.CaptureTraceContext("TriggerConsolidation");
        var run = BuildNewRun(type, templateIdValue, templateName, projectName, projectId, autoDispatch);
        run.TraceParent = traceContext?.GetValueOrDefault("traceparent");

        // ── 5. Build and submit the JobDistributionRequest ───────────────────
        // IssueIdentifier format: "{type}:{scope}" (issue #3027). The scope is what the run works on:
        // the brain for brain consolidation, the template (and so its repository) for a refactoring scan,
        // "global" for harness suggestions. Many templates can share one brain, and two consolidations of
        // one brain would race to push to it, so templates that share a brain share the key.
        // This deterministic format feeds the partial unique index on
        // (IssueIdentifier, IssueProviderConfigId) for non-terminal statuses in
        // PipelineDbContext.OnModelCreating, providing cross-replica dedup.
        var scope = type == ConsolidationRunType.BrainConsolidation && !string.IsNullOrEmpty(brainProviderId)
            ? brainProviderId
            : templateIdValue ?? "global";
        var issueIdentifier = $"{type}:{scope}";

        var request = new JobDistributionRequest
        {
            IssueIdentifier = issueIdentifier,
            IssueProviderConfigId = ConsolidationConstants.ProviderConfigId,
            RepoProviderConfigId = repoProviderId,
            BrainProviderConfigId = brainProviderId,
            InitiatedBy = ConsolidationConstants.InitiatedBy,
            TaskType = WorkItemTaskType.Consolidation,
            AgentSelector = AgentSelectorKey.From(selectorLabels),
            TimeoutSeconds = (int)config.AgentTimeout.TotalSeconds,
            ConsolidationRunType = type,
            ConsolidationTemplateId = templateIdValue,
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
            return null;
        }

        // ── 6. Detect duplicate rejection from the API layer ─────────────────
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
            return null;
        }

        // ── 7. Record WorkItemId on the in-memory run object ─────────────────
        // ConsolidationRun store writes have been stopped (issue #3028); the PipelineRun is the
        // authoritative record.
        run.WorkItemId = result.WorkItemId;

        _logger.Information("Consolidation run {RunId} created: {Type} for {TemplateName} (WorkItem {WorkItemId} created as Pending)",
            run.RunId, type, templateName, result.WorkItemId);
        OnChange?.Invoke();
        return run;
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

    private static ConsolidationRun BuildNewRun(
        ConsolidationRunType type,
        string? templateIdValue,
        string templateName,
        string? projectName,
        string? projectId,
        bool autoDispatch) => new()
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
