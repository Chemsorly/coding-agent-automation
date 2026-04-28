using CodingAgentWebUI.Hubs;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Pipeline.Services;
using Microsoft.AspNetCore.SignalR;
using ILogger = Serilog.ILogger;

namespace CodingAgentWebUI.Services;

/// <summary>
/// Implements <see cref="IJobDispatcher"/> by coordinating between
/// <see cref="JobDispatcherService"/>, <see cref="AgentRegistryService"/>,
/// <see cref="PipelineOrchestrationService"/>, and the <see cref="AgentHub"/>
/// to dispatch pipeline jobs to remote agents.
/// </summary>
public sealed class AgentJobDispatcher : IJobDispatcher
{
    private readonly JobDispatcherService _dispatcher;
    private readonly AgentRegistryService _registry;
    private readonly OrchestratorRunService _runService;
    private readonly Pipeline.Services.PipelineOrchestrationService _orchestration;
    private readonly TokenVendingService _tokenVending;
    private readonly IConfigurationStore _configStore;
    private readonly IProviderFactory _providerFactory;
    private readonly IHubContext<AgentHub, IAgentHubClient> _hubContext;
    private readonly ILogger _logger;

    public AgentJobDispatcher(
        JobDispatcherService dispatcher,
        AgentRegistryService registry,
        OrchestratorRunService runService,
        Pipeline.Services.PipelineOrchestrationService orchestration,
        TokenVendingService tokenVending,
        IConfigurationStore configStore,
        IProviderFactory providerFactory,
        IHubContext<AgentHub, IAgentHubClient> hubContext,
        ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(runService);
        ArgumentNullException.ThrowIfNull(orchestration);
        ArgumentNullException.ThrowIfNull(tokenVending);
        ArgumentNullException.ThrowIfNull(configStore);
        ArgumentNullException.ThrowIfNull(providerFactory);
        ArgumentNullException.ThrowIfNull(hubContext);
        ArgumentNullException.ThrowIfNull(logger);

        _dispatcher = dispatcher;
        _registry = registry;
        _runService = runService;
        _orchestration = orchestration;
        _tokenVending = tokenVending;
        _configStore = configStore;
        _providerFactory = providerFactory;
        _hubContext = hubContext;
        _logger = logger;
    }

    /// <inheritdoc />
    public bool HasRegisteredAgents => _registry.GetAllAgents().Count > 0;

    /// <inheritdoc />
    public bool IsIssueBeingProcessedOrQueued(string issueIdentifier)
    {
        ArgumentNullException.ThrowIfNull(issueIdentifier);
        return _dispatcher.IsIssueQueued(issueIdentifier)
            || _runService.IsIssueBeingProcessed(issueIdentifier);
    }

    /// <inheritdoc />
    public async Task<bool> TryDispatchAsync(
        string issueIdentifier,
        string issueProviderId,
        string repoProviderId,
        string agentProviderId,
        string? brainProviderId,
        string? pipelineProviderId,
        string initiatedBy,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(issueIdentifier);
        ArgumentNullException.ThrowIfNull(issueProviderId);
        ArgumentNullException.ThrowIfNull(repoProviderId);
        ArgumentNullException.ThrowIfNull(agentProviderId);
        ArgumentNullException.ThrowIfNull(initiatedBy);

        // Check if already being processed
        if (_orchestration.IsIssueBeingProcessed(issueIdentifier) || _dispatcher.IsIssueQueued(issueIdentifier))
        {
            _logger.Information("Issue {IssueIdentifier} already being processed or queued, skipping dispatch", issueIdentifier);
            return false;
        }

        // Resolve required labels for agent matching
        var config = await _configStore.LoadPipelineConfigAsync(ct);
        var repoConfigs = await _configStore.LoadProviderConfigsAsync(ProviderKind.Repository, ct);
        var repoConfig = repoConfigs.FirstOrDefault(c => c.Id == repoProviderId);
        var requiredLabels = JobDispatcherService.ResolveRequiredLabels(repoConfig, config);

        // Try to find an idle agent
        var agent = _dispatcher.SelectAgent(requiredLabels);

        if (agent != null)
        {
            // Agent available — dispatch immediately
            return await DispatchToAgentAsync(
                agent, issueIdentifier, issueProviderId, repoProviderId,
                agentProviderId, brainProviderId, pipelineProviderId, initiatedBy, ct);
        }

        // No idle agent — enqueue for later dispatch
        var enqueued = _dispatcher.EnqueueJob(new PendingJob
        {
            IssueIdentifier = issueIdentifier,
            IssueProviderId = issueProviderId,
            RepoProviderId = repoProviderId,
            AgentProviderId = agentProviderId,
            BrainProviderId = brainProviderId,
            PipelineProviderId = pipelineProviderId,
            EnqueuedAt = DateTimeOffset.UtcNow,
            InitiatedBy = initiatedBy,
            RequiredLabels = requiredLabels
        });

        if (enqueued)
            _logger.Information("Issue {IssueIdentifier} enqueued for dispatch (no idle agents)", issueIdentifier);

        return enqueued;
    }

    /// <summary>
    /// Dispatches a job to a specific agent. Creates the PipelineRun, prepares configs,
    /// and sends the <see cref="JobAssignmentMessage"/> via SignalR.
    /// </summary>
    internal async Task<bool> DispatchToAgentAsync(
        AgentEntry agent,
        string issueIdentifier,
        string issueProviderId,
        string repoProviderId,
        string agentProviderId,
        string? brainProviderId,
        string? pipelineProviderId,
        string initiatedBy,
        CancellationToken ct)
    {
        try
        {
            // Create the dispatched run via PipelineOrchestrationService
            var run = await _orchestration.CreateDispatchedRunAsync(
                issueProviderId, repoProviderId, issueIdentifier,
                agentProviderId, agent.AgentId, ct,
                brainProviderId, pipelineProviderId, initiatedBy);

            if (run == null)
            {
                _logger.Warning("Failed to create dispatched run for issue {IssueIdentifier}", issueIdentifier);
                return false;
            }

            // Pre-fetch issue details and comments via IIssueProvider (REQ-4.4)
            var issueConfigs = await _configStore.LoadProviderConfigsAsync(ProviderKind.Issue, ct);
            var issueConfig = issueConfigs.FirstOrDefault(c => c.Id == issueProviderId);
            if (issueConfig == null)
            {
                _logger.Error("Issue provider config '{ConfigId}' not found", issueProviderId);
                _runService.RemoveRun(run.RunId);
                return false;
            }

            IssueDetail issueDetail;
            ParsedIssue parsedIssue;
            IReadOnlyList<IssueComment> issueComments;
            await using (var issueProvider = _providerFactory.CreateIssueProvider(issueConfig))
            {
                issueDetail = await issueProvider.GetIssueAsync(issueIdentifier, ct);
                parsedIssue = new IssueDescriptionParser().Parse(issueDetail.Description);
                var allComments = await issueProvider.ListCommentsAsync(issueIdentifier, ct);
                // Cap at 50 comments per REQ-4.4
                issueComments = allComments.Count > 50
                    ? allComments.Take(50).ToList().AsReadOnly()
                    : allComments;

                // Swap label to agent:in-progress before dispatch (REQ-7.2)
                try
                {
                    foreach (var label in AgentLabels.All)
                        await issueProvider.RemoveLabelAsync(issueIdentifier, label, ct);
                    await issueProvider.AddLabelAsync(issueIdentifier, AgentLabels.InProgress, ct);
                }
                catch (Exception ex)
                {
                    _logger.Warning(ex, "Failed to swap label to agent:in-progress for issue {IssueIdentifier}", issueIdentifier);
                }
            }

            // Update run with fetched issue title
            run.IssueTitle = issueDetail.Title;

            // Build provider configs for the agent (excluding issue provider)
            var rawConfigs = await BuildAgentProviderConfigsAsync(
                repoProviderId, agentProviderId, brainProviderId, pipelineProviderId, ct);
            var providerConfigs = await _tokenVending.PrepareAgentConfigsAsync(rawConfigs, repoProviderId, ct);

            var config = await _configStore.LoadPipelineConfigAsync(ct);

            // Detect existing analysis and rework state from comments
            string? existingAnalysis = null;
            bool forceRefreshAnalysis = false;
            var analysisComment = issueComments.FirstOrDefault(c => c.Body.Contains("## 🤖 Agent Analysis"));
            if (analysisComment is not null)
            {
                existingAnalysis = analysisComment.Body;
                var gateRejection = issueComments.FirstOrDefault(c => c.Body.Contains("<!-- agent:gate-rejection -->"));
                var gateWontDo = issueComments.FirstOrDefault(c => c.Body.Contains("<!-- agent:gate-wont-do -->"));
                if ((gateRejection?.CreatedAt > analysisComment.CreatedAt) ||
                    (gateWontDo?.CreatedAt > analysisComment.CreatedAt))
                    forceRefreshAnalysis = true;
            }

            var message = new JobAssignmentMessage
            {
                JobId = run.RunId,
                IssueIdentifier = issueIdentifier,
                IssueDetail = issueDetail,
                ParsedIssue = parsedIssue,
                IssueComments = issueComments,
                ExistingAnalysis = existingAnalysis,
                ForceRefreshAnalysis = forceRefreshAnalysis,
                RepoProviderConfigId = repoProviderId,
                AgentProviderConfigId = agentProviderId,
                BrainProviderConfigId = brainProviderId,
                PipelineProviderConfigId = pipelineProviderId,
                ProviderConfigs = providerConfigs,
                PipelineConfiguration = config,
                InitiatedBy = initiatedBy
            };

            // Assign the job to the agent in the registry
            agent.ActiveJobId = run.RunId;
            _registry.TransitionStatus(agent.AgentId, AgentStatus.Busy);

            // Send the assignment via SignalR
            await _hubContext.Clients.Client(agent.ConnectionId).AssignJob(message);

            _logger.Information(
                "Job {JobId} dispatched to agent {AgentId} for issue {IssueIdentifier}",
                run.RunId, agent.AgentId, issueIdentifier);

            return true;
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Failed to dispatch job to agent {AgentId} for issue {IssueIdentifier}",
                agent.AgentId, issueIdentifier);

            // Reset agent status on failure
            agent.ActiveJobId = null;
            _registry.TransitionStatus(agent.AgentId, AgentStatus.Idle);

            // Revert label on dispatch failure (REQ-7.7)
            try
            {
                var issueConfigs2 = await _configStore.LoadProviderConfigsAsync(ProviderKind.Issue, CancellationToken.None);
                var issueConfig2 = issueConfigs2.FirstOrDefault(c => c.Id == issueProviderId);
                if (issueConfig2 != null)
                {
                    await using var revertProvider = _providerFactory.CreateIssueProvider(issueConfig2);
                    foreach (var label in AgentLabels.All)
                        await revertProvider.RemoveLabelAsync(issueIdentifier, label, CancellationToken.None);
                    await revertProvider.AddLabelAsync(issueIdentifier, AgentLabels.Next, CancellationToken.None);
                }
            }
            catch { /* best effort */ }

            return false;
        }
    }

    /// <summary>
    /// Builds the list of provider configs to send to the agent.
    /// Excludes issue provider configs (agents don't get issue access).
    /// </summary>
    private async Task<IReadOnlyList<ProviderConfig>> BuildAgentProviderConfigsAsync(
        string repoProviderId, string agentProviderId,
        string? brainProviderId, string? pipelineProviderId,
        CancellationToken ct)
    {
        var configs = new List<ProviderConfig>();

        var repoConfigs = await _configStore.LoadProviderConfigsAsync(ProviderKind.Repository, ct);
        var repoConfig = repoConfigs.FirstOrDefault(c => c.Id == repoProviderId);
        if (repoConfig != null)
            configs.Add(repoConfig);

        var agentConfigs = await _configStore.LoadProviderConfigsAsync(ProviderKind.Agent, ct);
        var agentConfig = agentConfigs.FirstOrDefault(c => c.Id == agentProviderId);
        if (agentConfig != null)
            configs.Add(agentConfig);

        if (!string.IsNullOrEmpty(brainProviderId))
        {
            var brainConfig = repoConfigs.FirstOrDefault(c => c.Id == brainProviderId);
            if (brainConfig != null)
                configs.Add(brainConfig);
        }

        if (!string.IsNullOrEmpty(pipelineProviderId))
        {
            var pipelineConfigs = await _configStore.LoadProviderConfigsAsync(ProviderKind.Pipeline, ct);
            var pipelineConfig = pipelineConfigs.FirstOrDefault(c => c.Id == pipelineProviderId);
            if (pipelineConfig != null)
                configs.Add(pipelineConfig);
        }

        return configs.AsReadOnly();
    }
}
