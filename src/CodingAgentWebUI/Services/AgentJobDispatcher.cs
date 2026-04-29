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
    /// <summary>
    /// Holds the pre-fetched issue context needed to build a <see cref="JobAssignmentMessage"/>.
    /// </summary>
    private sealed record IssueContext(
        IssueDetail IssueDetail,
        ParsedIssue ParsedIssue,
        IReadOnlyList<IssueComment> IssueComments,
        string? ExistingAnalysis,
        bool ForceRefreshAnalysis);
    private readonly JobDispatcherService _dispatcher;
    private readonly AgentRegistryService _registry;
    private readonly OrchestratorRunService _runService;
    private readonly Pipeline.Services.PipelineOrchestrationService _orchestration;
    private readonly TokenVendingService _tokenVending;
    private readonly IConfigurationStore _configStore;
    private readonly IProviderFactory _providerFactory;
    private readonly ProfileResolver _profileResolver;
    private readonly QualityGateResolver _qualityGateResolver;
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
        ProfileResolver profileResolver,
        QualityGateResolver qualityGateResolver,
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
        ArgumentNullException.ThrowIfNull(profileResolver);
        ArgumentNullException.ThrowIfNull(qualityGateResolver);
        ArgumentNullException.ThrowIfNull(hubContext);
        ArgumentNullException.ThrowIfNull(logger);

        _dispatcher = dispatcher;
        _registry = registry;
        _runService = runService;
        _orchestration = orchestration;
        _tokenVending = tokenVending;
        _configStore = configStore;
        _providerFactory = providerFactory;
        _profileResolver = profileResolver;
        _qualityGateResolver = qualityGateResolver;
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
        string? brainProviderId,
        string? pipelineProviderId,
        string initiatedBy,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(issueIdentifier);
        ArgumentNullException.ThrowIfNull(issueProviderId);
        ArgumentNullException.ThrowIfNull(repoProviderId);
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
                brainProviderId, pipelineProviderId, initiatedBy, requiredLabels, ct);
        }

        // No idle agent — enqueue for later dispatch
        var enqueued = _dispatcher.EnqueueJob(new PendingJob
        {
            IssueIdentifier = issueIdentifier,
            IssueProviderId = issueProviderId,
            RepoProviderId = repoProviderId,
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
    /// Dispatches a job to a specific agent. Resolves the agent profile and quality gate
    /// configurations, creates the PipelineRun, prepares configs, and sends the
    /// <see cref="JobAssignmentMessage"/> via SignalR.
    /// </summary>
    internal async Task<bool> DispatchToAgentAsync(
        AgentEntry agent,
        string issueIdentifier,
        string issueProviderId,
        string repoProviderId,
        string? brainProviderId,
        string? pipelineProviderId,
        string initiatedBy,
        IReadOnlyList<string> requiredLabels,
        CancellationToken ct)
    {
        try
        {
            // Resolve profile for this agent
            var profiles = await _configStore.LoadAgentProfilesAsync(ct);
            var profile = _profileResolver.Resolve(profiles, agent.Labels);
            if (profile is null)
            {
                var labelsStr = string.Join(", ", agent.Labels);
                _logger.Warning("No profile matches agent {AgentId} labels [{Labels}]", agent.AgentId, labelsStr);
                return false;
            }

            var agentProviderId = profile.AgentProviderConfigId;

            // Resolve quality gate configurations for this job
            var allQgcs = await _configStore.LoadQualityGateConfigsAsync(ct);
            var resolvedQgcs = _qualityGateResolver.Resolve(allQgcs, requiredLabels);

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

            // Populate resolved profile and QGC IDs on the run
            run.ResolvedProfileId = profile.Id;
            run.ResolvedQualityGateConfigIds = resolvedQgcs.Select(q => q.Id).ToList().AsReadOnly();

            // Pre-fetch issue details and comments
            var issueContext = await PrepareIssueContextAsync(issueIdentifier, issueProviderId, ct);
            if (issueContext is null)
            {
                _logger.Error("Issue provider config '{ConfigId}' not found", issueProviderId);
                _runService.RemoveRun(run.RunId);
                return false;
            }

            // Update run with fetched issue title
            run.IssueTitle = issueContext.IssueDetail.Title;

            // Build and prepare provider configs for the agent
            var providerConfigs = await PrepareProviderConfigsAsync(
                repoProviderId, agentProviderId, brainProviderId, pipelineProviderId, ct);

            var config = await _configStore.LoadPipelineConfigAsync(ct);

            var message = new JobAssignmentMessage
            {
                JobId = run.RunId,
                IssueIdentifier = issueIdentifier,
                IssueDetail = issueContext.IssueDetail,
                ParsedIssue = issueContext.ParsedIssue,
                IssueComments = issueContext.IssueComments,
                ExistingAnalysis = issueContext.ExistingAnalysis,
                ForceRefreshAnalysis = issueContext.ForceRefreshAnalysis,
                RepoProviderConfigId = repoProviderId,
                AgentProviderConfigId = agentProviderId,
                BrainProviderConfigId = brainProviderId,
                PipelineProviderConfigId = pipelineProviderId,
                ProviderConfigs = providerConfigs,
                PipelineConfiguration = config,
                InitiatedBy = initiatedBy,
                ResolvedProfileId = profile.Id,
                QualityGateConfigs = resolvedQgcs,
                McpServers = profile.McpServers
            };

            // Assign the job to the agent in the registry
            agent.ActiveJobId = run.RunId;
            _registry.TransitionStatus(agent.AgentId, AgentStatus.Busy);

            // Send the assignment via SignalR
            await _hubContext.Clients.Client(agent.ConnectionId).AssignJob(message);

            _logger.Information(
                "Job {JobId} dispatched to agent {AgentId} for issue {IssueIdentifier} (profile={ProfileId}, qgcs={QgcCount})",
                run.RunId, agent.AgentId, issueIdentifier, profile.Id, resolvedQgcs.Count);

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
    /// Pre-fetches issue details, comments, swaps labels, and detects existing analysis.
    /// Returns null if the issue provider config is not found.
    /// </summary>
    private async Task<IssueContext?> PrepareIssueContextAsync(
        string issueIdentifier,
        string issueProviderId,
        CancellationToken ct)
    {
        var issueConfigs = await _configStore.LoadProviderConfigsAsync(ProviderKind.Issue, ct);
        var issueConfig = issueConfigs.FirstOrDefault(c => c.Id == issueProviderId);
        if (issueConfig == null)
            return null;

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

        return new IssueContext(issueDetail, parsedIssue, issueComments, existingAnalysis, forceRefreshAnalysis);
    }

    /// <summary>
    /// Builds the provider configs list and prepares tokens via the token vending service.
    /// </summary>
    private async Task<IReadOnlyList<ProviderConfig>> PrepareProviderConfigsAsync(
        string repoProviderId,
        string agentProviderId,
        string? brainProviderId,
        string? pipelineProviderId,
        CancellationToken ct)
    {
        var rawConfigs = await BuildAgentProviderConfigsAsync(
            repoProviderId, agentProviderId, brainProviderId, pipelineProviderId, ct);
        return await _tokenVending.PrepareAgentConfigsAsync(rawConfigs, repoProviderId, ct);
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
