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
/// When no idle agent is available, enqueues the job into <see cref="ConsolidationQueueService"/>.
/// </summary>
public sealed class ConsolidationDispatcher : IConsolidationDispatcher
{
    private readonly AgentRegistryService _registry;
    private readonly JobDispatcherService _jobDispatcher;
    private readonly IAgentCommunication _agentComm;
    private readonly IConfigurationStore _configStore;
    private readonly ITokenVendingService _tokenVending;
    private readonly PipelineConfiguration _config;
    private readonly ILogger _logger;
    private readonly string _consolidationRunsDirectory;
    private readonly ConsolidationQueueService _queueService;

    private static readonly System.Text.Json.JsonSerializerOptions s_jsonOptions = new()
    {
        PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
    };

    public ConsolidationDispatcher(
        AgentRegistryService registry,
        JobDispatcherService jobDispatcher,
        IAgentCommunication agentComm,
        IConfigurationStore configStore,
        ITokenVendingService tokenVending,
        PipelineConfiguration config,
        ILogger logger,
        ConsolidationQueueService queueService,
        string consolidationRunsDirectory = PipelineConstants.ConsolidationRunsDirectory)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(jobDispatcher);
        ArgumentNullException.ThrowIfNull(agentComm);
        ArgumentNullException.ThrowIfNull(configStore);
        ArgumentNullException.ThrowIfNull(tokenVending);
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(queueService);

        _registry = registry;
        _jobDispatcher = jobDispatcher;
        _agentComm = agentComm;
        _configStore = configStore;
        _tokenVending = tokenVending;
        _config = config;
        _logger = logger;
        _queueService = queueService;
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
                TemplateName = run.TemplateName,
                WorkspacePath = workspacePath,
                RequiredLabels = requiredLabels.ToList(),
                EnqueuedAt = DateTimeOffset.UtcNow
            };
            _queueService.EnqueueJob(pendingJob);

            // Persist labels on the run so they survive restart rehydration
            run.QueuedRequiredLabels = requiredLabels.ToList();

            _logger.Information(
                "No idle agent for consolidation run {RunId} (type={Type}, labels=[{Labels}]) — queued",
                run.RunId, type, string.Join(", ", requiredLabels));
            return ConsolidationDispatchResult.Queued;
        }

        try
        {
            await DispatchToAgentAsync(run.RunId, type, templateId, run.TemplateName, feedbackDataJson, workspacePath, agent, ct);
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

        // Cancel-during-dispatch race guard
        if (_queueService.IsCancelled(job.RunId))
        {
            _logger.Information("Consolidation job {RunId} was cancelled, skipping dispatch", job.RunId);
            return false;
        }

        var agent = _registry.GetByAgentId(agentId);
        if (agent is null || agent.Status != AgentStatus.Idle)
            return false;

        try
        {
            // Regenerate feedback data at dispatch time for harness suggestions
            string? feedbackDataJson = null;
            if (job.Type == ConsolidationRunType.HarnessSuggestions)
            {
                feedbackDataJson = await RegenerateFeedbackDataAsync(ct);
            }

            await DispatchToAgentAsync(job.RunId, job.Type, job.TemplateId, job.TemplateName, feedbackDataJson, job.WorkspacePath, agent, ct);
            return true;
        }
        catch (Exception ex)
        {
            _logger.Error(ex,
                "Failed to dispatch queued consolidation job {RunId} to agent {AgentId}",
                job.RunId, agentId);

            agent.ActiveJobId = null;
            _registry.TransitionStatus(agent.AgentId, AgentStatus.Idle);
            return false;
        }
    }

    /// <summary>
    /// Core dispatch logic shared between immediate dispatch and drain-time dispatch.
    /// </summary>
    private async Task DispatchToAgentAsync(
        string runId,
        ConsolidationRunType type,
        string? templateId,
        string? templateName,
        string? feedbackDataJson,
        string workspacePath,
        AgentEntry agent,
        CancellationToken ct)
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
            JobId = runId,
            Type = type,
            TemplateId = templateId,
            TemplateName = templateName,
            ProviderConfigs = providerConfigs,
            PipelineConfiguration = _config,
            LastSuccessfulRunUtc = lastSuccessfulRunUtc,
            FeedbackDataJson = feedbackDataJson,
            WorkspacePath = workspacePath
        };

        // Assign the job to the agent
        agent.ActiveJobId = runId;
        _registry.TransitionStatus(agent.AgentId, AgentStatus.Busy);

        await _agentComm.AssignConsolidationJobAsync(agent.ConnectionId, agent.AgentId, message, ct);

        _logger.Information(
            "Consolidation job {RunId} dispatched to agent {AgentId} (type={Type}, template={TemplateName})",
            runId, agent.AgentId, type, templateName);
    }

    /// <summary>
    /// Regenerates feedback data at dispatch time (for harness suggestions queued runs).
    /// </summary>
    // TODO: This method reads all consolidation run JSON files from disk but always returns null. The file I/O loop computing sinceUtc is dead code — either implement actual feedback regeneration or remove the method body.
    private async Task<string?> RegenerateFeedbackDataAsync(CancellationToken ct)
    {
        try
        {
            // Read last successful harness run timestamp from disk
            DateTime sinceUtc = DateTime.MinValue;
            if (Directory.Exists(_consolidationRunsDirectory))
            {
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
                            && run.CompletedAtUtc.Value > sinceUtc)
                        {
                            sinceUtc = run.CompletedAtUtc.Value;
                        }
                    }
                    catch { /* skip malformed */ }
                }
            }

            // We don't have direct access to IPipelineRunHistoryService here,
            // so return null and let the agent handle it with the LastSuccessfulRunUtc field
            return null;
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Failed to regenerate feedback data for queued harness suggestions");
            return null;
        }
    }

    /// <summary>
    /// Resolves required agent labels for the given template. For template-scoped runs,
    /// uses the template's repo provider config labels. For global runs, uses default labels.
    /// </summary>
    private async Task<IReadOnlyList<string>> ResolveRequiredLabelsAsync(string? templateId, CancellationToken ct)
    {
        if (templateId is null)
        {
            // Global (harness suggestions) — use default labels
            return JobDispatcherService.ResolveRequiredLabels(null, _config);
        }

        var template = _config.PipelineJobTemplates.FirstOrDefault(t => t.Id == templateId);
        if (template is null)
            return JobDispatcherService.ResolveRequiredLabels(null, _config);

        var repoConfigs = await _configStore.LoadProviderConfigsAsync(ProviderKind.Repository, ct);
        var repoConfig = repoConfigs.FirstOrDefault(c => c.Id == template.RepoProviderId);
        return JobDispatcherService.ResolveRequiredLabels(repoConfig, _config);
    }

    /// <summary>
    /// Builds the provider configs list for the consolidation job based on the run type and template.
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

        var template = _config.PipelineJobTemplates.FirstOrDefault(t => t.Id == templateId);
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
            var issueConfigs = await _configStore.LoadProviderConfigsAsync(ProviderKind.Issue, ct);
            var issueConfig = issueConfigs.FirstOrDefault(c => c.Id == template.IssueProviderId);
            if (issueConfig is not null)
                configs.Add(issueConfig);
        }

        return configs.AsReadOnly();
    }

    /// <summary>
    /// Gets the CompletedAtUtc of the last successful run for the given type and template.
    /// Used to set LastSuccessfulRunUtc on the job message for incremental processing.
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
