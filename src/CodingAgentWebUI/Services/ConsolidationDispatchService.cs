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
/// </summary>
public sealed class ConsolidationDispatchService : IConsolidationDispatcher
{
    private readonly AgentRegistryService _registry;
    private readonly JobDispatcherService _jobDispatcher;
    private readonly IAgentCommunication _agentComm;
    private readonly IConfigurationStore _configStore;
    private readonly ITokenVendingService _tokenVending;
    private readonly PipelineConfiguration _config;
    private readonly ILogger _logger;
    private readonly string _consolidationRunsDirectory;

    private static readonly System.Text.Json.JsonSerializerOptions s_jsonOptions = new()
    {
        PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
    };

    public ConsolidationDispatchService(
        AgentRegistryService registry,
        JobDispatcherService jobDispatcher,
        IAgentCommunication agentComm,
        IConfigurationStore configStore,
        ITokenVendingService tokenVending,
        PipelineConfiguration config,
        ILogger logger,
        string consolidationRunsDirectory = "config/pipeline/consolidation-runs")
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(jobDispatcher);
        ArgumentNullException.ThrowIfNull(agentComm);
        ArgumentNullException.ThrowIfNull(configStore);
        ArgumentNullException.ThrowIfNull(tokenVending);
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(logger);

        _registry = registry;
        _jobDispatcher = jobDispatcher;
        _agentComm = agentComm;
        _configStore = configStore;
        _tokenVending = tokenVending;
        _config = config;
        _logger = logger;
        _consolidationRunsDirectory = consolidationRunsDirectory;
    }

    /// <inheritdoc />
    public async Task<bool> TryDispatchAsync(
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
            _logger.Warning(
                "No idle agent available for consolidation run {RunId} (type={Type}, labels=[{Labels}])",
                run.RunId, type, string.Join(", ", requiredLabels));
            return false;
        }

        try
        {
            // Build provider configs for the consolidation job and vend tokens
            // Include issues:write permission only for refactoring jobs that create issues
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

            return true;
        }
        catch (Exception ex)
        {
            _logger.Error(ex,
                "Failed to dispatch consolidation job {RunId} to agent {AgentId}",
                run.RunId, agent.AgentId);

            // Reset agent status on failure
            agent.ActiveJobId = null;
            _registry.TransitionStatus(agent.AgentId, AgentStatus.Idle);

            return false;
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
