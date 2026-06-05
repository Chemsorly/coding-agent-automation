using System.Diagnostics;
using CodingAgentWebUI.Orchestration;
using CodingAgentWebUI.Orchestration.Dispatch;
using CodingAgentWebUI.Orchestration.Registry;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Pipeline.Telemetry;
using OpenTelemetry;
using OpenTelemetry.Context.Propagation;
using ILogger = Serilog.ILogger;

namespace CodingAgentWebUI.Services;

/// <summary>
/// Implements <see cref="IConsolidationDispatcher"/> by selecting an idle agent from the
/// <see cref="AgentRegistryService"/>, building a <see cref="ConsolidationJobMessage"/>,
/// and dispatching it via <see cref="IAgentCommunication"/>.
/// When no idle agent is available, enqueues the job in <see cref="ConsolidationQueueService"/>.
/// </summary>
public sealed class ConsolidationDispatcher : IConsolidationDispatcher
{
    private readonly AgentRegistryService _registry;
    private readonly JobDispatcherService _jobDispatcher;
    private readonly IAgentCommunication _agentComm;
    private readonly IConfigurationStore _configStore;
    private readonly IProjectStore _projectStore;
    private readonly ITokenVendingService _tokenVending;
    private readonly PipelineConfiguration _config;
    private readonly ConsolidationQueueService _queueService;
    private readonly IPipelineRunHistoryService _runHistoryService;
    private readonly ILogger _logger;
    private readonly string _consolidationRunsDirectory;

    public ConsolidationDispatcher(
        AgentRegistryService registry,
        JobDispatcherService jobDispatcher,
        IAgentCommunication agentComm,
        IConfigurationStore configStore,
        IProjectStore projectStore,
        ITokenVendingService tokenVending,
        PipelineConfiguration config,
        ConsolidationQueueService queueService,
        IPipelineRunHistoryService runHistoryService,
        ILogger logger,
        string consolidationRunsDirectory = PipelineConstants.ConsolidationRunsDirectory)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(jobDispatcher);
        ArgumentNullException.ThrowIfNull(agentComm);
        ArgumentNullException.ThrowIfNull(configStore);
        ArgumentNullException.ThrowIfNull(projectStore);
        ArgumentNullException.ThrowIfNull(tokenVending);
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(queueService);
        ArgumentNullException.ThrowIfNull(runHistoryService);
        ArgumentNullException.ThrowIfNull(logger);

        _registry = registry;
        _jobDispatcher = jobDispatcher;
        _agentComm = agentComm;
        _configStore = configStore;
        _projectStore = projectStore;
        _tokenVending = tokenVending;
        _config = config;
        _queueService = queueService;
        _runHistoryService = runHistoryService;
        _logger = logger;
        _consolidationRunsDirectory = consolidationRunsDirectory;
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

        // Select an idle agent matching the labels
        var agent = _jobDispatcher.SelectAgent(requiredLabels);
        if (agent is null)
        {
            // No idle agent — enqueue for later dispatch
            var pendingJob = new PendingConsolidationJob
            {
                RunId = run.RunId,
                Type = type,
                TemplateId = templateId,
                WorkspacePath = workspacePath,
                RequiredLabels = requiredLabels,
                EnqueuedAt = DateTimeOffset.UtcNow
            };

            // Store required labels on the run for restart rehydration
            run.QueuedRequiredLabels = requiredLabels.ToList();

            _queueService.EnqueueJob(pendingJob);

            _logger.Information(
                "No idle agent for consolidation run {RunId} (type={Type}), enqueued for later dispatch",
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

        // Cancel-during-dispatch race check
        if (_queueService.IsRunCancelled(runId))
        {
            _logger.Information("Consolidation job {RunId} was cancelled, skipping dispatch", runId);
            return false;
        }

        var agent = _registry.GetByAgentId(agentId);
        if (agent is null || agent.Status != AgentStatus.Idle)
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
    public void NotifyRunCancelled(string runId)
    {
        ArgumentNullException.ThrowIfNull(runId);
        _queueService.CancelRun(runId);
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
        // Build provider configs for the consolidation job and vend tokens
        var includeIssuePermission = type == ConsolidationRunType.RefactoringDetection;
        var rawConfigs = await BuildProviderConfigsAsync(type, templateId, ct);
        var template = templateId is not null ? await ResolveTemplateAsync(templateId, ct) : null;
        var repoProviderId = template?.RepoProviderId ?? "";
        var providerConfigs = await _tokenVending.PrepareAgentConfigsAsync(rawConfigs, repoProviderId, ct, includeIssuePermission);

        // Resolve last successful run timestamp for this type+template
        var lastSuccessfulRunUtc = await GetLastSuccessfulRunUtcAsync(type, templateId, ct);

        // Build the ConsolidationJobMessage
        var message = new ConsolidationJobMessage
        {
            JobId = run.RunId,
            Type = type,
            TemplateId = templateId,
            TemplateName = run.TemplateName,
            ProviderConfigs = providerConfigs,
            PipelineConfiguration = _config,
            LastSuccessfulRunUtc = lastSuccessfulRunUtc,
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
        var filePath = Path.Combine(_consolidationRunsDirectory, $"{runId}.json");
        if (!File.Exists(filePath))
            return null;

        try
        {
            var json = await File.ReadAllTextAsync(filePath, ct);
            return System.Text.Json.JsonSerializer.Deserialize<ConsolidationRun>(json, Pipeline.PipelineJsonOptions.Default);
        }
        catch (OperationCanceledException) { throw; } // TODO: Bare catch was swallowing OperationCanceledException — propagate cancellation properly
        catch
        {
            return null;
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
                ConsolidationRunType.HarnessSuggestions, null, ct) ?? DateTime.MinValue;

            var allRuns = _runHistoryService.GetRunHistory();
            var feedbackEntries = allRuns
                .Where(r => r.Feedback is not null && r.StartedAt > sinceUtc)
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
    /// Builds the provider configs list for the consolidation job based on the run type and template.
    /// Uses project-based template lookup via IProjectStore.
    /// </summary>
    private async Task<IReadOnlyList<ProviderConfig>> BuildProviderConfigsAsync(
        ConsolidationRunType type,
        string? templateId,
        CancellationToken ct)
    {
        var configs = new List<ProviderConfig>();

        // Always need an agent provider
        var agentConfigs = await _configStore.LoadProviderConfigsAsync(ProviderKind.Agent, ct);
        var agentConfig = agentConfigs.FirstOrDefault();
        if (agentConfig is not null)
            configs.Add(agentConfig);

        if (templateId is null)
            return configs.AsReadOnly();

        var template = await ResolveTemplateAsync(templateId, ct);
        if (template is null)
            return configs.AsReadOnly();

        // Add repo provider (Work role)
        if (!string.IsNullOrEmpty(template.RepoProviderId))
        {
            var repoConfigs = await _configStore.LoadProviderConfigsAsync(ProviderKind.Repository, ct);
            var repoConfig = repoConfigs.FirstOrDefault(c => c.Id == template.RepoProviderId);
            if (repoConfig is not null)
                configs.Add(repoConfig);

            // Add brain provider if configured
            if (!string.IsNullOrEmpty(template.BrainProviderId))
            {
                var brainConfig = repoConfigs.FirstOrDefault(c => c.Id == template.BrainProviderId);
                if (brainConfig is not null)
                    configs.Add(brainConfig);
            }
        }

        // Add issue provider for refactoring detection
        if (type == ConsolidationRunType.RefactoringDetection && !string.IsNullOrEmpty(template.IssueProviderId))
        {
            var issueConfig = await _configStore.GetProviderConfigByIdAsync(template.IssueProviderId, ProviderKind.Issue, ct);
            if (issueConfig is not null)
                configs.Add(issueConfig);
        }

        return configs.AsReadOnly();
    }

    /// <summary>
    /// Gets the CompletedAtUtc of the last successful run for the given type and template.
    /// </summary>
    private async Task<DateTime?> GetLastSuccessfulRunUtcAsync(
        ConsolidationRunType type,
        string? templateId,
        CancellationToken ct)
    {
        if (!Directory.Exists(_consolidationRunsDirectory))
            return null;

        DateTime? latest = null;
        foreach (var file in Directory.GetFiles(_consolidationRunsDirectory, "*.json"))
        {
            try
            {
                var json = await File.ReadAllTextAsync(file, ct);
                var historicRun = System.Text.Json.JsonSerializer.Deserialize<ConsolidationRun>(json, Pipeline.PipelineJsonOptions.Default);

                if (historicRun is not null
                    && historicRun.Type == type
                    && historicRun.TemplateId == templateId
                    && historicRun.Status == ConsolidationRunStatus.Succeeded
                    && historicRun.CompletedAtUtc.HasValue)
                {
                    if (latest is null || historicRun.CompletedAtUtc.Value > latest.Value)
                        latest = historicRun.CompletedAtUtc.Value;
                }
            }
            catch
            {
                // Skip malformed files
            }
        }

        return latest;
    }

    /// <summary>
    /// Resolves a template by ID from projects via IProjectStore.
    /// Flattens all enabled projects' templates and finds the matching template.
    /// Falls back to PipelineConfiguration.PipelineJobTemplates for templates not yet
    /// assigned to projects (pre-migration scenario).
    /// </summary>
    private async Task<PipelineJobTemplate?> ResolveTemplateAsync(string templateId, CancellationToken ct)
    {
        // Primary: look up from projects (project-based ownership)
        var projects = await _projectStore.LoadProjectsAsync(ct);
        var templateLookup = _config.PipelineJobTemplates.ToDictionary(t => t.Id);

        foreach (var project in projects.Where(p => p.Enabled))
        {
            if (project.TemplateIds.Contains(templateId) && templateLookup.TryGetValue(templateId, out var template))
                return template;
        }

        // Fallback: check global config directly (handles pre-migration or orphaned templates)
        if (templateLookup.TryGetValue(templateId, out var fallbackTemplate))
        {
            _logger.Warning("Template '{TemplateId}' not found in any enabled project, using fallback from global config", templateId);
            return fallbackTemplate;
        }

        return null;
    }

    private static readonly TraceContextPropagator TraceContextPropagator = new();

    // TODO: CaptureTraceContext duplicates AgentJobDispatcher.CaptureTraceContext (internal static).
    // Consider calling AgentJobDispatcher.CaptureTraceContext() directly or extracting to a shared helper.
    private static Dictionary<string, string>? CaptureTraceContext()
    {
        using var activity = PipelineTelemetry.ActivitySource.StartActivity(
            "DispatchConsolidation", ActivityKind.Producer);
        if (activity is null)
            return null;

        var carrier = new Dictionary<string, string>();
        TraceContextPropagator.Inject(
            new PropagationContext(activity.Context, Baggage.Current),
            carrier,
            static (c, key, value) => c[key] = value);
        return carrier.Count > 0 ? carrier : null;
    }
}
