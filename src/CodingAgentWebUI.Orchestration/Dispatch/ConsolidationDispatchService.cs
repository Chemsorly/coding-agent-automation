using CodingAgentWebUI.Orchestration;
using CodingAgentWebUI.Orchestration.Registry;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Pipeline.Services;
using CodingAgentWebUI.Pipeline.Telemetry;
using ILogger = Serilog.ILogger;

namespace CodingAgentWebUI.Orchestration.Dispatch;

/// <summary>
/// Groups the core dependencies of <see cref="ConsolidationDispatchService"/> to reduce
/// constructor parameter count (S107). All members are required.
/// </summary>
public sealed record ConsolidationDispatchDependencies(
    IAgentRegistryService Registry,
    JobDeduplicationGuardService JobDispatcher,
    IAgentCommunication AgentComm,
    IConfigurationStore ConfigStore,
    IProjectStore ProjectStore,
    ITokenVendingService TokenVending,
    PipelineConfiguration Config,
    IWorkDistributor WorkDistributor,
    IPipelineRunHistoryService RunHistoryService,
    ILogger Logger,
    IConsolidationRunStore RunStore);

/// <summary>
/// Implements <see cref="IConsolidationDispatchService"/> by selecting an idle agent from the
/// <see cref="AgentRegistryService"/>, building a <see cref="ConsolidationJobMessage"/>,
/// and dispatching it via <see cref="IAgentCommunication"/>.
/// When no idle agent is available, enqueues via <see cref="IWorkDistributor"/>.
/// </summary>
public sealed class ConsolidationDispatchService : IConsolidationDispatchService
{
    private readonly IAgentRegistryService _registry;
    private readonly JobDeduplicationGuardService _jobDispatcher;
    private readonly IAgentCommunication _agentComm;
    private readonly IConfigurationStore _configStore;
    private readonly IProjectStore _projectStore;
    private readonly IConsolidationJobPreparationService _jobPreparer;
    private readonly IWorkDistributor _workDistributor;
    private readonly IPipelineRunHistoryService _runHistoryService;
    private readonly ILogger _logger;
    private readonly IConsolidationRunStore _runStore;
    private readonly Lazy<IConsolidationRunTracker>? _runTracker;

    public ConsolidationDispatchService(
        ConsolidationDispatchDependencies deps,
        IConsolidationJobPreparationService? jobPreparer = null,
        Lazy<IConsolidationRunTracker>? runTracker = null)
    {
        ArgumentNullException.ThrowIfNull(deps);
        ArgumentNullException.ThrowIfNull(deps.Registry);
        ArgumentNullException.ThrowIfNull(deps.JobDispatcher);
        ArgumentNullException.ThrowIfNull(deps.AgentComm);
        ArgumentNullException.ThrowIfNull(deps.ConfigStore);
        ArgumentNullException.ThrowIfNull(deps.ProjectStore);
        ArgumentNullException.ThrowIfNull(deps.TokenVending);
        ArgumentNullException.ThrowIfNull(deps.Config);
        ArgumentNullException.ThrowIfNull(deps.WorkDistributor);
        ArgumentNullException.ThrowIfNull(deps.RunHistoryService);
        ArgumentNullException.ThrowIfNull(deps.Logger);
        ArgumentNullException.ThrowIfNull(deps.RunStore);

        _registry = deps.Registry;
        _jobDispatcher = deps.JobDispatcher;
        _agentComm = deps.AgentComm;
        _configStore = deps.ConfigStore;
        _projectStore = deps.ProjectStore;
        _jobPreparer = jobPreparer ?? new ConsolidationJobPreparationService(deps.ConfigStore, deps.ProjectStore, deps.TokenVending, deps.Logger);
        _workDistributor = deps.WorkDistributor;
        _runHistoryService = deps.RunHistoryService;
        _logger = deps.Logger;
        _runStore = deps.RunStore;
        _runTracker = runTracker;
    }

    /// <inheritdoc />
    public async Task<ConsolidationDispatchResult> TryDispatchAsync(
        ConsolidationRun run,
        ConsolidationRunType type,
        TemplateId? templateId,
        string? feedbackDataJson,
        string workspacePath,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(workspacePath);

        // Load live config from the store at dispatch time. The startup singleton (_config) may be
        // stale if settings were changed via the UI after the orchestrator started (the UI writes to
        // the DB-backed store, but the singleton is loaded once from the JSON file at boot).
        var liveConfig = await _configStore.LoadPipelineConfigAsync(ct);

        // Resolve required labels from the template's repo provider config (if template-scoped)
        var requiredLabels = await ResolveRequiredLabelsAsync(templateId, liveConfig, ct);

        // Resolve the agent profile to get the full MatchLabels (the template key).
        // Required labels are a subset used for agent selection; MatchLabels is the full set
        // that maps to a JobTemplate in K8s mode. Same pattern as DispatchOrchestrationService.
        var agentSelectorLabels = await ResolveAgentSelectorLabelsAsync(requiredLabels, ct);

        // Select an idle agent matching the labels
        var agent = _jobDispatcher.SelectAgent(requiredLabels);
        if (agent is null)
        {
            // No idle agent — enqueue via IWorkDistributor for unified drain
            // Store resolved selector labels on the run for restart rehydration and UI display
            run.QueuedRequiredLabels = agentSelectorLabels.ToList();

            var distributionRequest = new JobDistributionRequest
            {
                IssueIdentifier = run.RunId,
                IssueProviderConfigId = ConsolidationConstants.ProviderConfigId,
                RepoProviderConfigId = "",
                InitiatedBy = ConsolidationConstants.InitiatedBy,
                TaskType = WorkItemTaskType.Consolidation,
                AgentSelector = string.Join(",", agentSelectorLabels.OrderBy(l => l, StringComparer.Ordinal)),
                TimeoutSeconds = (int)liveConfig.AgentTimeout.TotalSeconds,
                ConsolidationRunType = type,
                ConsolidationTemplateId = templateId?.Value,
                ConsolidationWorkspacePath = workspacePath,
                RunId = run.RunId,
                AutoDispatch = run.AutoDispatch
            };

            var result = await _workDistributor.DistributeAsync(distributionRequest, ct);
            if (!result.Success)
            {
                _logger.Error(
                    "Failed to enqueue consolidation run {RunId} via IWorkDistributor: {Error}",
                    run.RunId, result.ErrorMessage);
                return ConsolidationDispatchResult.Failed;
            }

            _logger.Information(
                "No idle agent for consolidation run {RunId} (type={Type}), enqueued via IWorkDistributor",
                run.RunId, type);
            return ConsolidationDispatchResult.Queued;
        }

        try
        {
            await DispatchToAgentAsync(
                new ConsolidationDispatchContext(run, type, templateId, feedbackDataJson, workspacePath, liveConfig),
                agent, ct);
            return ConsolidationDispatchResult.Dispatched;
        }
        catch (Exception ex)
        {
            _logger.Error(ex,
                "Failed to dispatch consolidation job {RunId} to agent {AgentId}",
                run.RunId, agent.AgentId);

            // Reset agent status on failure
            agent.ActiveJobId = null;
            _registry.TransitionStatus(agent.AgentId, AgentStatus.Idle);

            return ConsolidationDispatchResult.Failed;
        }
    }

    /// <summary>
    /// Dispatches a queued consolidation job to a specific agent. Called by the drain service.
    /// Token vending happens here (at dispatch time, not enqueue time).
    /// </summary>
    public async Task<bool> TryDispatchToAgentAsync(
        string runId,
        ConsolidationRunType type,
        TemplateId? templateId,
        string workspacePath,
        AgentId agentId,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(runId);
        ArgumentNullException.ThrowIfNull(workspacePath);
        // TODO: AgentId is a readonly record struct so it cannot be null, but AgentId.Value can be null
        // if constructed via `new AgentId(null!)` or `default(AgentId)`. ThrowIfNullOrEmpty covers both
        // the null-Value and empty-Value cases, but throws NullReferenceException internally when Value is
        // null rather than a descriptive ArgumentException. Consider adding `ArgumentNullException.ThrowIfNull(agentId.Value, nameof(agentId))`
        // before this line once AgentId's primary constructor is hardened (see TODO in AgentId.cs).
        ArgumentException.ThrowIfNullOrEmpty(agentId.Value);

        // Cancel-during-dispatch race check via run store
        var existingRun = await _runStore.GetByIdAsync(runId, ct);
        if (existingRun is null ||
            existingRun.Status == ConsolidationRunStatus.Cancelled ||
            existingRun.Status == ConsolidationRunStatus.Failed)
        {
            _logger.Information("Consolidation job {RunId} was cancelled/failed, skipping dispatch", runId);
            return false;
        }

        var agent = _registry.GetByAgentId(agentId);
        if (agent is null)
            return false;

        // Accept Idle (Legacy drain — agent not yet reserved) or Busy with no active job
        // (DB drain — agent pre-reserved by PendingWorkItemDrainService via ResolveAgent).
        if (agent.Status != AgentStatus.Idle &&
            !(agent.Status == AgentStatus.Busy && agent.ActiveJobId is null))
            return false;

        // Load the run from disk to get template name
        var run = await LoadRunAsync(runId, ct);
        if (run is null)
            return false;

        try
        {
            // Regenerate feedback data at dispatch time for harness suggestions
            string? feedbackDataJson = null;
            if (type == ConsolidationRunType.HarnessSuggestions)
            {
                feedbackDataJson = await RegenerateFeedbackDataAsync(runId, ct);
            }

            // Load live config at dispatch time (same fix as TryDispatchAsync — avoids stale startup singleton)
            var liveConfig = await _configStore.LoadPipelineConfigAsync(ct);

            await DispatchToAgentAsync(
                new ConsolidationDispatchContext(run, type, templateId, feedbackDataJson, workspacePath, liveConfig),
                agent, ct);

            // Transition run from Queued → Running after successful dispatch
            // (previously done in the deleted DrainConsolidationJobsAsync)
            await TransitionRunToRunningAsync(runId, ct);

            return true;
        }
        catch (Exception ex)
        {
            _logger.Error(ex,
                "Failed to dispatch queued consolidation job {RunId} to agent {AgentId}",
                runId, agentId);

            agent.ActiveJobId = null;
            _registry.TransitionStatus(agent.AgentId, AgentStatus.Idle);
            return false;
        }
    }

    /// <inheritdoc />
    public async Task NotifyRunCancelledAsync(string runId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(runId);

        // DB mode: transition WorkItem to Cancelled
        // TODO: CancelJobAsync uses Guid.TryParse(runId) as WorkItem.Id. This works because
        // InsertConsolidationAsPendingAsync sets WorkItem.Id = RunId (when parseable as GUID).
        // If the coupling breaks (e.g., RunId not parseable), cancellation silently fails.
        // Consider querying by IssueIdentifier instead of relying on ID equality. (#1084 follow-up)
        await _workDistributor.CancelJobAsync(runId, ct);

        // Legacy mode: remove from in-memory queue
        _jobDispatcher.RemoveJob(runId);
    }

    /// <summary>
    /// Groups the parameters of <see cref="DispatchToAgentAsync"/> that describe what to dispatch,
    /// reducing its parameter count (S107).
    /// </summary>
    private sealed record ConsolidationDispatchContext(
        ConsolidationRun Run,
        ConsolidationRunType Type,
        TemplateId? TemplateId,
        string? FeedbackDataJson,
        string WorkspacePath,
        PipelineConfiguration LiveConfig);

    private async Task DispatchToAgentAsync(
        ConsolidationDispatchContext ctx,
        AgentEntry agent,
        CancellationToken ct)
    {
        // Delegate config resolution and token vending to shared preparer
        var preparation = await _jobPreparer.PrepareAsync(ctx.Type, ctx.TemplateId, agent.Labels, ct);

        // Resolve last successful run timestamp for this type+template
        var lastSuccessfulRunUtc = await GetLastSuccessfulRunUtcAsync(ctx.Type, ctx.TemplateId, ct);

        // Build the ConsolidationJobMessage
        var message = new ConsolidationJobMessage
        {
            JobId = ctx.Run.RunId,
            Type = ctx.Type,
            TemplateId = ctx.TemplateId?.Value,
            TemplateName = ctx.Run.TemplateName,
            ProviderConfigs = preparation.ProviderConfigs,
            PipelineConfiguration = ctx.LiveConfig,
            LastSuccessfulRunUtc = lastSuccessfulRunUtc?.UtcDateTime,
            FeedbackDataJson = ctx.FeedbackDataJson,
            WorkspacePath = ctx.WorkspacePath,
            TraceContext = CaptureTraceContext(),
            AutoDispatch = ctx.Run.AutoDispatch
        };

        // Assign the job to the agent
        agent.ActiveJobId = ctx.Run.RunId;
        _registry.TransitionStatus(agent.AgentId, AgentStatus.Busy);

        await _agentComm.AssignConsolidationJobAsync(agent.ConnectionId, agent.AgentId, message, ct);

        _logger.Information(
            "Consolidation job {RunId} dispatched to agent {AgentId} (type={Type}, template={TemplateName})",
            ctx.Run.RunId, agent.AgentId, ctx.Type, ctx.Run.TemplateName);
    }

    private async Task<ConsolidationRun?> LoadRunAsync(string runId, CancellationToken ct)
    {
        return await _runStore.GetByIdAsync(runId, ct);
    }

    /// <summary>
    /// Transitions a queued consolidation run to Running status after successful dispatch.
    /// Delegates to <see cref="IConsolidationRunTracker"/> which handles both persistent store
    /// and in-memory tracker updates, eliminating duplication with ConsolidationService.
    /// </summary>
    internal async Task TransitionRunToRunningAsync(string runId, CancellationToken ct)
    {
        try
        {
            if (_runTracker?.Value is { } tracker)
            {
                await tracker.TransitionToRunningAsync(runId, ct);
                // TODO: This log is emitted unconditionally but TransitionToRunningAsync is a no-op
                // if the run is not found or not in Queued status. Consider returning a bool from the
                // tracker method or removing this log (the tracker already logs on success).
                _logger.Information("Consolidation run {RunId} transitioned from Queued to Running", runId);
            }
            else
            {
                // Fallback: direct store write when tracker not available (test isolation only).
                // In-memory tracker will NOT be updated — acceptable for tests.
                var run = await _runStore.GetByIdAsync(runId, ct);
                if (run is null || run.Status != ConsolidationRunStatus.Queued)
                    return;

                run.Status = ConsolidationRunStatus.Running;
                run.StartedAtUtc = DateTimeOffset.UtcNow;
                await _runStore.SaveRunAsync(run, ct);

                _logger.Warning(
                    "Consolidation run {RunId} transitioned via fallback (no tracker) — in-memory state NOT updated",
                    runId);
            }
        }
        catch (Exception ex)
        {
            // Non-fatal: the run will still execute, just shows wrong status in the UI until completion
            _logger.Error(ex, "Failed to transition consolidation run {RunId} to Running", runId);
        }
    }

    /// <summary>
    /// Regenerates feedback data at dispatch time for harness suggestion runs.
    /// This ensures fresh data that includes feedback collected while the job was queued.
    /// </summary>
    private async Task<string?> RegenerateFeedbackDataAsync(string runId, CancellationToken ct)
    {
        try
        {
            // Determine the "since" timestamp from the last successful harness suggestion run
            var sinceUtc = await GetLastSuccessfulRunUtcAsync(
                ConsolidationRunType.HarnessSuggestions, null, ct) ?? DateTimeOffset.MinValue;

            var allRuns = await _runHistoryService.GetRunHistoryAsync(ct);
            var feedbackEntries = allRuns
                .Where(r => r.Feedback is not null && r.StartedAtOffset > sinceUtc)
                .Select(r => r.Feedback!)
                .ToList();

            if (feedbackEntries.Count == 0)
            {
                _logger.Information(
                    "No new RunFeedback entries found since {SinceUtc} for queued harness suggestion run {RunId}",
                    sinceUtc, runId);
                return null;
            }

            var feedbackJson = System.Text.Json.JsonSerializer.Serialize(feedbackEntries, Pipeline.PipelineJsonOptions.Default);
            _logger.Information(
                "Regenerated {Count} RunFeedback entries for queued harness suggestion run {RunId}",
                feedbackEntries.Count, runId);
            return feedbackJson;
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Failed to regenerate feedback data for harness suggestion run {RunId}", runId);
            return null;
        }
    }

    /// <summary>
    /// Resolves required agent labels for the given template.
    /// Uses project-based template lookup via IProjectStore.
    /// </summary>
    internal async Task<IReadOnlyList<string>> ResolveRequiredLabelsAsync(TemplateId? templateId, PipelineConfiguration config, CancellationToken ct)
    {
        if (templateId is null)
            return JobDeduplicationGuardService.ResolveRequiredLabels(null, config);

        var template = await ResolveTemplateAsync(templateId.Value, ct);
        if (template is null)
            return JobDeduplicationGuardService.ResolveRequiredLabels(null, config);

        var repoConfig = await _configStore.GetProviderConfigByIdAsync(template.RepoProviderId, ProviderKind.Repository, ct);
        return JobDeduplicationGuardService.ResolveRequiredLabels(repoConfig, config);
    }

    /// <summary>
    /// Resolves the full agent selector labels (profile MatchLabels) from required labels.
    /// Required labels are a subset used for agent matching; the profile's MatchLabels
    /// form the complete label set that maps to a JobTemplate key in K8s mode.
    /// Falls back to requiredLabels if no matching profile is found.
    /// Same pattern as DispatchOrchestrationService.ResolveProfileByLabelsAsync + MapToRequest.
    /// </summary>
    internal async Task<IReadOnlyList<string>> ResolveAgentSelectorLabelsAsync(
        IReadOnlyList<string> requiredLabels, CancellationToken ct)
    {
        var profiles = await _configStore.LoadAgentProfilesAsync(ct);

        var profile = ProfileResolver.ResolveByRequiredLabels(profiles, requiredLabels);

        if (profile is null)
        {
            _logger.Warning(
                "ConsolidationDispatchService: no profile covers requiredLabels [{Labels}], using raw labels as selector. " +
                "Template resolution may fail in K8s mode if no template is keyed by this subset.",
                string.Join(", ", requiredLabels));
            return requiredLabels;
        }

        _logger.Debug(
            "ConsolidationDispatchService: resolved profile '{ProfileId}' for requiredLabels [{RequiredLabels}] → MatchLabels [{MatchLabels}]",
            profile.Id, string.Join(", ", requiredLabels), string.Join(", ", profile.MatchLabels));

        return profile.MatchLabels;
    }

    /// <summary>
    /// Gets the CompletedAtUtc of the last successful run for the given type and template.
    /// </summary>
    private async Task<DateTimeOffset?> GetLastSuccessfulRunUtcAsync(
        ConsolidationRunType type,
        TemplateId? templateId,
        CancellationToken ct)
    {
        var allRuns = await _runStore.LoadAllRunsAsync(ct);
        return allRuns
            .Where(r => r.Type == type && r.TemplateId == templateId?.Value
                && r.Status == ConsolidationRunStatus.Succeeded && r.CompletedAtUtc.HasValue)
            .Max(r => r.CompletedAtUtc);
    }

    /// <summary>
    /// Resolves a template by ID from projects via IProjectStore.
    /// Flattens all enabled projects' templates and finds the matching template.
    /// </summary>
    private async Task<PipelineJobTemplate?> ResolveTemplateAsync(TemplateId templateId, CancellationToken ct)
    {
        var projects = await _projectStore.LoadProjectsAsync(ct);
        var templateLookup = (await _projectStore.LoadAllTemplatesAsync(ct)).ToDictionary(t => t.Id);

        foreach (var project in projects.Where(p => p.Enabled))
        {
            if (project.TemplateIds.Contains(templateId.Value) && templateLookup.TryGetValue(templateId.Value, out var template))
                return template;
        }

        return null;
    }

    private static Dictionary<string, string>? CaptureTraceContext() =>
        PipelineTelemetry.CaptureTraceContext("DispatchConsolidation");
}
