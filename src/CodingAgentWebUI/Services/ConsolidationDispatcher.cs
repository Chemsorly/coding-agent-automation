using CodingAgentWebUI.Orchestration;
using CodingAgentWebUI.Orchestration.Dispatch;
using CodingAgentWebUI.Orchestration.Registry;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
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
    private readonly ConsolidationQueueService _queueService;
    private readonly IAgentCommunication _agentComm;
    private readonly IConfigurationStore _configStore;
    private readonly ITokenVendingService _tokenVending;
    private readonly IConsolidationService _consolidationService;
    private readonly PipelineConfiguration _config;
    private readonly ILogger _logger;
    private readonly string _consolidationRunsDirectory;

    private static readonly System.Text.Json.JsonSerializerOptions s_jsonOptions = new()
    {
        PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
    };

    public ConsolidationDispatcher(
        AgentRegistryService registry,
        JobDispatcherService jobDispatcher,
        ConsolidationQueueService queueService,
        IAgentCommunication agentComm,
        IConfigurationStore configStore,
        ITokenVendingService tokenVending,
        IConsolidationService consolidationService,
        PipelineConfiguration config,
        ILogger logger,
        string consolidationRunsDirectory = PipelineConstants.ConsolidationRunsDirectory)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(jobDispatcher);
        ArgumentNullException.ThrowIfNull(queueService);
        ArgumentNullException.ThrowIfNull(agentComm);
        ArgumentNullException.ThrowIfNull(configStore);
        ArgumentNullException.ThrowIfNull(tokenVending);
        ArgumentNullException.ThrowIfNull(consolidationService);
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(logger);

        _registry = registry;
        _jobDispatcher = jobDispatcher;
        _queueService = queueService;
        _agentComm = agentComm;
        _configStore = configStore;
        _tokenVending = tokenVending;
        _consolidationService = consolidationService;
        _config = config;
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
            var sinceUtc = type == ConsolidationRunType.HarnessSuggestions
                ? await GetFeedbackSinceUtcAsync(ct)
                : (DateTime?)null;

            var pendingJob = new PendingConsolidationJob
            {
                RunId = run.RunId,
                Type = type,
                TemplateId = templateId,
                WorkspacePath = workspacePath,
                RequiredLabels = requiredLabels,
                EnqueuedAt = DateTimeOffset.UtcNow,
                FeedbackSinceUtc = sinceUtc
            };

            // Persist required labels on the run for restart rehydration
            run.QueuedRequiredLabels = requiredLabels;

            // TODO: Signal JobQueueDrainService after enqueue so that if an agent is already idle,
            // dispatch happens immediately instead of waiting up to the 10s periodic drain interval.
            _queueService.EnqueueJob(pendingJob);

            _logger.Information(
                "Consolidation run {RunId} queued (type={Type}, labels=[{Labels}])",
                run.RunId, type, string.Join(", ", requiredLabels));

            return ConsolidationDispatchResult.Queued;
        }

        try
        {
            // Build provider configs for the consolidation job and vend tokens
            var includeIssuePermission = type == ConsolidationRunType.RefactoringDetection;
            var rawConfigs = await BuildProviderConfigsAsync(type, templateId, ct);
            var repoProviderId = templateId is not null
                ? _config.PipelineJobTemplates.FirstOrDefault(t => t.Id == templateId)?.RepoProviderId ?? ""
                : "";
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
                WorkspacePath = workspacePath
            };

            // Assign the job to the agent
            agent.ActiveJobId = run.RunId;
            _registry.TransitionStatus(agent.AgentId, AgentStatus.Busy);

            await _agentComm.AssignConsolidationJobAsync(agent.ConnectionId, agent.AgentId, message, ct);

            _logger.Information(
                "Consolidation job {RunId} dispatched to agent {AgentId} (type={Type}, template={TemplateName})",
                run.RunId, agent.AgentId, type, run.TemplateName);

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

    /// <inheritdoc />
    public async Task<bool> TryDispatchToAgentAsync(PendingConsolidationJob job, string agentId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(job);
        ArgumentNullException.ThrowIfNull(agentId);

        // Check if cancelled before dispatching
        if (_queueService.IsCancelled(job.RunId))
        {
            _logger.Information("Consolidation job {RunId} was cancelled, skipping dispatch", job.RunId);
            return false;
        }

        var agent = _registry.GetAllAgents().FirstOrDefault(a => a.AgentId == agentId);
        if (agent is null)
        {
            _logger.Warning("Agent {AgentId} not found for consolidation dispatch", agentId);
            return false;
        }

        try
        {
            // Regenerate feedback data at dispatch time for HarnessSuggestions
            string? feedbackDataJson = null;
            if (job.Type == ConsolidationRunType.HarnessSuggestions && job.FeedbackSinceUtc.HasValue)
            {
                feedbackDataJson = await _consolidationService.GenerateFeedbackDataJsonAsync(
                    job.FeedbackSinceUtc.Value, ct);
            }

            // Build provider configs and vend fresh tokens
            var includeIssuePermission = job.Type == ConsolidationRunType.RefactoringDetection;
            var rawConfigs = await BuildProviderConfigsAsync(job.Type, job.TemplateId, ct);
            var repoProviderId = job.TemplateId is not null
                ? _config.PipelineJobTemplates.FirstOrDefault(t => t.Id == job.TemplateId)?.RepoProviderId ?? ""
                : "";
            var providerConfigs = await _tokenVending.PrepareAgentConfigsAsync(rawConfigs, repoProviderId, ct, includeIssuePermission);

            var lastSuccessfulRunUtc = await GetLastSuccessfulRunUtcAsync(job.Type, job.TemplateId, ct);

            var template = job.TemplateId is not null
                ? _config.PipelineJobTemplates.FirstOrDefault(t => t.Id == job.TemplateId)
                : null;

            var message = new ConsolidationJobMessage
            {
                JobId = job.RunId,
                Type = job.Type,
                TemplateId = job.TemplateId,
                TemplateName = template?.Name ?? "Global",
                ProviderConfigs = providerConfigs,
                PipelineConfiguration = _config,
                LastSuccessfulRunUtc = lastSuccessfulRunUtc,
                FeedbackDataJson = feedbackDataJson,
                WorkspacePath = job.WorkspacePath
            };

            agent.ActiveJobId = job.RunId;
            _registry.TransitionStatus(agent.AgentId, AgentStatus.Busy);

            await _agentComm.AssignConsolidationJobAsync(agent.ConnectionId, agent.AgentId, message, ct);

            _logger.Information(
                "Queued consolidation job {RunId} dispatched to agent {AgentId} (type={Type})",
                job.RunId, agent.AgentId, job.Type);

            return true;
        }
        catch (Exception ex)
        {
            _logger.Error(ex,
                "Failed to dispatch queued consolidation job {RunId} to agent {AgentId}",
                job.RunId, agentId);

            if (agent is not null)
            {
                agent.ActiveJobId = null;
                _registry.TransitionStatus(agent.AgentId, AgentStatus.Idle);
            }

            return false;
        }
    }

    private async Task<DateTime> GetFeedbackSinceUtcAsync(CancellationToken ct)
    {
        // Scan for last successful harness run to determine the SinceUtc timestamp
        if (!Directory.Exists(_consolidationRunsDirectory))
            return DateTime.MinValue;

        DateTime latest = DateTime.MinValue;
        foreach (var file in Directory.GetFiles(_consolidationRunsDirectory, "*.json"))
        {
            try
            {
                var json = await File.ReadAllTextAsync(file, ct);
                var run = System.Text.Json.JsonSerializer.Deserialize<ConsolidationRun>(json, s_jsonOptions);
                if (run is not null
                    && run.Type == ConsolidationRunType.HarnessSuggestions
                    && run.Status == ConsolidationRunStatus.Succeeded
                    && run.CompletedAtUtc.HasValue
                    && run.CompletedAtUtc.Value > latest)
                {
                    latest = run.CompletedAtUtc.Value;
                }
            }
            catch { }
        }

        return latest;
    }

    /// <summary>
    /// Resolves required agent labels for the given template.
    /// </summary>
    private async Task<IReadOnlyList<string>> ResolveRequiredLabelsAsync(string? templateId, CancellationToken ct)
    {
        if (templateId is null)
            return JobDispatcherService.ResolveRequiredLabels(null, _config);

        var template = _config.PipelineJobTemplates.FirstOrDefault(t => t.Id == templateId);
        if (template is null)
            return JobDispatcherService.ResolveRequiredLabels(null, _config);

        var repoConfigs = await _configStore.LoadProviderConfigsAsync(ProviderKind.Repository, ct);
        var repoConfig = repoConfigs.FirstOrDefault(c => c.Id == template.RepoProviderId);
        return JobDispatcherService.ResolveRequiredLabels(repoConfig, _config);
    }

    /// <summary>
    /// Builds the provider configs list for the consolidation job.
    /// </summary>
    private async Task<IReadOnlyList<ProviderConfig>> BuildProviderConfigsAsync(
        ConsolidationRunType type,
        string? templateId,
        CancellationToken ct)
    {
        var configs = new List<ProviderConfig>();

        var agentConfigs = await _configStore.LoadProviderConfigsAsync(ProviderKind.Agent, ct);
        var agentConfig = agentConfigs.FirstOrDefault();
        if (agentConfig is not null)
            configs.Add(agentConfig);

        if (templateId is null)
            return configs.AsReadOnly();

        var template = _config.PipelineJobTemplates.FirstOrDefault(t => t.Id == templateId);
        if (template is null)
            return configs.AsReadOnly();

        if (!string.IsNullOrEmpty(template.RepoProviderId))
        {
            var repoConfigs = await _configStore.LoadProviderConfigsAsync(ProviderKind.Repository, ct);
            var repoConfig = repoConfigs.FirstOrDefault(c => c.Id == template.RepoProviderId);
            if (repoConfig is not null)
                configs.Add(repoConfig);

            if (!string.IsNullOrEmpty(template.BrainProviderId))
            {
                var brainConfig = repoConfigs.FirstOrDefault(c => c.Id == template.BrainProviderId);
                if (brainConfig is not null)
                    configs.Add(brainConfig);
            }
        }

        if (type == ConsolidationRunType.RefactoringDetection && !string.IsNullOrEmpty(template.IssueProviderId))
        {
            var issueConfigs = await _configStore.LoadProviderConfigsAsync(ProviderKind.Issue, ct);
            var issueConfig = issueConfigs.FirstOrDefault(c => c.Id == template.IssueProviderId);
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
                var historicRun = System.Text.Json.JsonSerializer.Deserialize<ConsolidationRun>(json, s_jsonOptions);

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
}
