using CodingAgentWebUI.Orchestration;
using CodingAgentWebUI.Orchestration.Dispatch;
using CodingAgentWebUI.Orchestration.Registry;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Pipeline.Services;
using CodingAgentWebUI.Pipeline.Telemetry;
using ILogger = Serilog.ILogger;

namespace CodingAgentWebUI.Services;

/// <summary>
/// Implements <see cref="IConsolidationDispatcher"/> by selecting an idle agent from the
/// <see cref="AgentRegistryService"/>, building a <see cref="ConsolidationJobMessage"/>,
/// and dispatching it via <see cref="IAgentCommunication"/>.
/// When no idle agent is available, enqueues via <see cref="IWorkDistributor"/>.
/// </summary>
public sealed class ConsolidationDispatcher : IConsolidationDispatcher
{
    private readonly IAgentRegistryService _registry;
    private readonly JobDispatcherService _jobDispatcher;
    private readonly IAgentCommunication _agentComm;
    private readonly IConfigurationStore _configStore;
    private readonly IProjectStore _projectStore;
    private readonly ITokenVendingService _tokenVending;
    private readonly IConsolidationJobPreparer _jobPreparer;
    private readonly PipelineConfiguration _config;
    private readonly IWorkDistributor _workDistributor;
    private readonly IPipelineRunHistoryService _runHistoryService;
    private readonly ILogger _logger;
    private readonly IConsolidationRunStore _runStore;

    public ConsolidationDispatcher(
        IAgentRegistryService registry,
        JobDispatcherService jobDispatcher,
        IAgentCommunication agentComm,
        IConfigurationStore configStore,
        IProjectStore projectStore,
        ITokenVendingService tokenVending,
        PipelineConfiguration config,
        IWorkDistributor workDistributor,
        IPipelineRunHistoryService runHistoryService,
        ILogger logger,
        IConsolidationRunStore runStore,
        IConsolidationJobPreparer? jobPreparer = null)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(jobDispatcher);
        ArgumentNullException.ThrowIfNull(agentComm);
        ArgumentNullException.ThrowIfNull(configStore);
        ArgumentNullException.ThrowIfNull(projectStore);
        ArgumentNullException.ThrowIfNull(tokenVending);
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(workDistributor);
        ArgumentNullException.ThrowIfNull(runHistoryService);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(runStore);

        _registry = registry;
        _jobDispatcher = jobDispatcher;
        _agentComm = agentComm;
        _configStore = configStore;
        _projectStore = projectStore;
        _tokenVending = tokenVending;
        _jobPreparer = jobPreparer ?? new ConsolidationJobPreparer(configStore, projectStore, tokenVending, logger);
        _config = config;
        _workDistributor = workDistributor;
        _runHistoryService = runHistoryService;
        _logger = logger;
        _runStore = runStore;
    }

    /// <inheritdoc />
    public async Task<ConsolidationDispatchResult> TryDispatchAsync(
        ConsolidationRun run,
        ConsolidationRunType type,
        string? templateId,
        string? feedbackDataJson,
        string workspacePath,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(workspacePath);

        // Resolve required labels from the template's repo provider config (if template-scoped)
        var requiredLabels = await ResolveRequiredLabelsAsync(templateId, ct);

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
                TimeoutSeconds = (int)_config.AgentTimeout.TotalSeconds,
                ConsolidationRunType = type,
                ConsolidationTemplateId = templateId,
                ConsolidationWorkspacePath = workspacePath,
                RunId = run.RunId
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
            await DispatchToAgentAsync(run, type, templateId, feedbackDataJson, workspacePath, agent, ct);
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
        string? templateId,
        string workspacePath,
        string agentId,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(runId);
        ArgumentNullException.ThrowIfNull(workspacePath);
        ArgumentNullException.ThrowIfNull(agentId);

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

            await DispatchToAgentAsync(run, type, templateId, feedbackDataJson, workspacePath, agent, ct);

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

    private async Task DispatchToAgentAsync(
        ConsolidationRun run,
        ConsolidationRunType type,
        string? templateId,
        string? feedbackDataJson,
        string workspacePath,
        AgentEntry agent,
        CancellationToken ct)
    {
        // Delegate config resolution and token vending to shared preparer
        var preparation = await _jobPreparer.PrepareAsync(type, templateId, agent.Labels, ct);

        // Resolve last successful run timestamp for this type+template
        var lastSuccessfulRunUtc = await GetLastSuccessfulRunUtcAsync(type, templateId, ct);

        // Build the ConsolidationJobMessage
        var message = new ConsolidationJobMessage
        {
            JobId = run.RunId,
            Type = type,
            TemplateId = templateId,
            TemplateName = run.TemplateName,
            ProviderConfigs = preparation.ProviderConfigs,
            PipelineConfiguration = _config,
            LastSuccessfulRunUtc = lastSuccessfulRunUtc?.UtcDateTime,
            FeedbackDataJson = feedbackDataJson,
            WorkspacePath = workspacePath,
            TraceContext = CaptureTraceContext()
        };

        // Assign the job to the agent
        agent.ActiveJobId = run.RunId;
        _registry.TransitionStatus(agent.AgentId, AgentStatus.Busy);

        await _agentComm.AssignConsolidationJobAsync(agent.ConnectionId, agent.AgentId, message, ct);

        _logger.Information(
            "Consolidation job {RunId} dispatched to agent {AgentId} (type={Type}, template={TemplateName})",
            run.RunId, agent.AgentId, type, run.TemplateName);
    }

    private async Task<ConsolidationRun?> LoadRunAsync(string runId, CancellationToken ct)
    {
        return await _runStore.GetByIdAsync(runId, ct);
    }

    /// <summary>
    /// Transitions a queued consolidation run to Running status after successful dispatch.
    /// This mirrors what the deleted DrainConsolidationJobsAsync did via IConsolidationService.TransitionToRunningAsync.
    /// We call the run store directly to avoid a circular dependency (ConsolidationService → IConsolidationDispatcher → IConsolidationService).
    /// </summary>
    private async Task TransitionRunToRunningAsync(string runId, CancellationToken ct)
    {
        try
        {
            var run = await _runStore.GetByIdAsync(runId, ct);
            if (run is null || run.Status != ConsolidationRunStatus.Queued)
                return;

            run.Status = ConsolidationRunStatus.Running;
            await _runStore.SaveRunAsync(run, ct);

            _logger.Information("Consolidation run {RunId} transitioned from Queued to Running", runId);
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
    internal async Task<IReadOnlyList<string>> ResolveRequiredLabelsAsync(string? templateId, CancellationToken ct)
    {
        if (templateId is null)
            return JobDispatcherService.ResolveRequiredLabels(null, _config);

        var template = await ResolveTemplateAsync(templateId, ct);
        if (template is null)
            return JobDispatcherService.ResolveRequiredLabels(null, _config);

        var repoConfig = await _configStore.GetProviderConfigByIdAsync(template.RepoProviderId, ProviderKind.Repository, ct);
        return JobDispatcherService.ResolveRequiredLabels(repoConfig, _config);
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

        var resolver = new ProfileResolver();
        var profile = resolver.ResolveByRequiredLabels(profiles, requiredLabels);

        if (profile is null)
        {
            _logger.Warning(
                "ConsolidationDispatcher: no profile covers requiredLabels [{Labels}], using raw labels as selector. " +
                "Template resolution may fail in K8s mode if no template is keyed by this subset.",
                string.Join(", ", requiredLabels));
            return requiredLabels;
        }

        _logger.Debug(
            "ConsolidationDispatcher: resolved profile '{ProfileId}' for requiredLabels [{RequiredLabels}] → MatchLabels [{MatchLabels}]",
            profile.Id, string.Join(", ", requiredLabels), string.Join(", ", profile.MatchLabels));

        return profile.MatchLabels;
    }

    /// <summary>
    /// <summary>
    /// Gets the CompletedAtUtc of the last successful run for the given type and template.
    /// </summary>
    private async Task<DateTimeOffset?> GetLastSuccessfulRunUtcAsync(
        ConsolidationRunType type,
        string? templateId,
        CancellationToken ct)
    {
        var allRuns = await _runStore.LoadAllRunsAsync(ct);
        return allRuns
            .Where(r => r.Type == type && r.TemplateId == templateId
                && r.Status == ConsolidationRunStatus.Succeeded && r.CompletedAtUtc.HasValue)
            .Max(r => r.CompletedAtUtc);
    }

    /// <summary>
    /// Resolves a template by ID from projects via IProjectStore.
    /// Flattens all enabled projects' templates and finds the matching template.
    /// </summary>
    private async Task<PipelineJobTemplate?> ResolveTemplateAsync(string templateId, CancellationToken ct)
    {
        var projects = await _projectStore.LoadProjectsAsync(ct);
        var templateLookup = (await _projectStore.LoadAllTemplatesAsync(ct)).ToDictionary(t => t.Id);

        foreach (var project in projects.Where(p => p.Enabled))
        {
            if (project.TemplateIds.Contains(templateId) && templateLookup.TryGetValue(templateId, out var template))
                return template;
        }

        return null;
    }

    private static Dictionary<string, string>? CaptureTraceContext() =>
        PipelineTelemetry.CaptureTraceContext("DispatchConsolidation");
}
