using CodingAgentWebUI.Orchestration.Registry;
using CodingAgentWebUI.Pipeline;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Pipeline.Services;
using ILogger = Serilog.ILogger;

namespace CodingAgentWebUI.Orchestration.Dispatch;

/// <summary>
/// Implements <see cref="IJobDispatcher"/> by coordinating between
/// <see cref="JobDispatcherService"/>, <see cref="AgentRegistryService"/>,
/// <see cref="PipelineOrchestrationService"/>, and the <c>AgentHub</c>
/// to dispatch pipeline jobs to remote agents.
/// </summary>
/// <remarks>
/// <para>
/// <b>Circular Dependency Risk:</b> This class depends on <see cref="PipelineOrchestrationService"/>
/// (concrete) to create dispatched runs. Meanwhile, <c>PipelineLoopService</c> (in the Pipeline project)
/// depends on <see cref="IJobDispatcher"/> (this class's interface) to dispatch issues discovered
/// during polling. This creates a potential circular dependency chain:
/// </para>
/// <para>
/// <c>AgentJobDispatcher</c> → <c>PipelineOrchestrationService</c> → (via <c>PipelineLoopService</c>) → <c>IJobDispatcher</c>
/// </para>
/// <para>
/// The cycle is currently mitigated by interface segregation: <c>PipelineLoopService</c> depends on
/// the <c>IJobDispatcher</c> interface (not the concrete <c>AgentJobDispatcher</c>), and accepts it
/// as an optional nullable parameter. The DI container resolves this without circular instantiation
/// because <c>PipelineOrchestrationService</c> does not directly depend on <c>IJobDispatcher</c> —
/// only <c>PipelineLoopService</c> does, and it is a separate service registration.
/// </para>
/// <para>
/// <b>Do not:</b> Add a direct dependency from <c>PipelineOrchestrationService</c> to
/// <c>IJobDispatcher</c> or <c>AgentJobDispatcher</c>, as this would create an unresolvable
/// circular DI registration.
/// </para>
/// </remarks>
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
    private readonly ITokenVendingService _tokenVending;
    private readonly IConfigurationStore _configStore;
    private readonly IProviderFactory _providerFactory;
    private readonly ILabelSwapper _labelSwapper;
    private readonly ProfileResolver _profileResolver;
    private readonly QualityGateResolver _qualityGateResolver;
    private readonly ReviewerResolver _reviewerResolver;
    private readonly IAgentCommunication _agentComm;
    private readonly ILogger _logger;

    public AgentJobDispatcher(
        JobDispatcherService dispatcher,
        AgentRegistryService registry,
        OrchestratorRunService runService,
        Pipeline.Services.PipelineOrchestrationService orchestration,
        ITokenVendingService tokenVending,
        IConfigurationStore configStore,
        IProviderFactory providerFactory,
        ILabelSwapper labelSwapper,
        ProfileResolver profileResolver,
        QualityGateResolver qualityGateResolver,
        ReviewerResolver reviewerResolver,
        IAgentCommunication agentComm,
        ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(runService);
        ArgumentNullException.ThrowIfNull(orchestration);
        ArgumentNullException.ThrowIfNull(tokenVending);
        ArgumentNullException.ThrowIfNull(configStore);
        ArgumentNullException.ThrowIfNull(providerFactory);
        ArgumentNullException.ThrowIfNull(labelSwapper);
        ArgumentNullException.ThrowIfNull(profileResolver);
        ArgumentNullException.ThrowIfNull(qualityGateResolver);
        ArgumentNullException.ThrowIfNull(reviewerResolver);
        ArgumentNullException.ThrowIfNull(agentComm);
        ArgumentNullException.ThrowIfNull(logger);

        _dispatcher = dispatcher;
        _registry = registry;
        _runService = runService;
        _orchestration = orchestration;
        _tokenVending = tokenVending;
        _configStore = configStore;
        _providerFactory = providerFactory;
        _labelSwapper = labelSwapper;
        _profileResolver = profileResolver;
        _qualityGateResolver = qualityGateResolver;
        _reviewerResolver = reviewerResolver;
        _agentComm = agentComm;
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
        CancellationToken ct,
        string? issueTitle = null)
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
            IssueTitle = issueTitle,
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

    /// <inheritdoc />
    public async Task<bool> TryDispatchReviewAsync(
        string prIdentifier,
        string prBranchName,
        string prTitle,
        string? prDescription,
        string prUrl,
        string prTargetBranch,
        string issueProviderId,
        string repoProviderId,
        string? brainProviderId,
        string initiatedBy,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(prIdentifier);
        ArgumentNullException.ThrowIfNull(prBranchName);
        ArgumentNullException.ThrowIfNull(prTitle);
        ArgumentNullException.ThrowIfNull(prUrl);
        ArgumentNullException.ThrowIfNull(prTargetBranch);
        ArgumentNullException.ThrowIfNull(issueProviderId);
        ArgumentNullException.ThrowIfNull(repoProviderId);
        ArgumentNullException.ThrowIfNull(initiatedBy);

        // Check if already being processed
        if (IsIssueBeingProcessedOrQueued(prIdentifier))
        {
            _logger.Information("PR {PrIdentifier} already being processed or queued, skipping review dispatch", prIdentifier);
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
            return await DispatchReviewToAgentAsync(
                agent, prIdentifier, prBranchName, prTitle, prDescription, prUrl, prTargetBranch,
                issueProviderId, repoProviderId, brainProviderId, initiatedBy, requiredLabels, ct);
        }

        // No idle agent — enqueue for later dispatch
        var enqueued = _dispatcher.EnqueueJob(new PendingJob
        {
            IssueIdentifier = prIdentifier,
            IssueTitle = prTitle,
            IssueProviderId = issueProviderId,
            RepoProviderId = repoProviderId,
            BrainProviderId = brainProviderId,
            PipelineProviderId = null,
            EnqueuedAt = DateTimeOffset.UtcNow,
            InitiatedBy = initiatedBy,
            RequiredLabels = requiredLabels,
            RunType = PipelineRunType.Review,
            PrBranchName = prBranchName,
            PrDescription = prDescription,
            PrUrl = prUrl,
            PrTargetBranch = prTargetBranch
        });

        if (enqueued)
            _logger.Information("PR review {PrIdentifier} enqueued for dispatch (no idle agents)", prIdentifier);

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

            // Resolve reviewer configurations for this job
            var allReviewerConfigs = await _configStore.LoadReviewerConfigsAsync(ct);
            var resolvedReviewerConfigs = _reviewerResolver.Resolve(allReviewerConfigs, requiredLabels);

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
            run.ResolvedReviewerConfigIds = resolvedReviewerConfigs.Select(r => r.Id).ToList().AsReadOnly();

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

            // Override BrainReadOnly from the matching template (per-template setting)
            // TODO: Template matching uses RepoProviderId + BrainProviderId which may be ambiguous
            // if multiple templates share the same combination. Consider passing template ID through
            // the dispatch chain. See review finding #4.
            var matchingTemplate = config.PipelineJobTemplates.FirstOrDefault(t =>
                t.RepoProviderId == repoProviderId && t.BrainProviderId == brainProviderId);
            // TODO: This override is one-directional (can only enable BrainReadOnly, never disable).
            // If global config has BrainReadOnly=true, a template with BrainReadOnly=false cannot
            // override it. Consider: if (matchingTemplate is not null) config = config with { BrainReadOnly = matchingTemplate.BrainReadOnly };
            // See review finding #3.
            if (matchingTemplate is { BrainReadOnly: true })
                config = config with { BrainReadOnly = true };

            // Override blacklist settings from repo provider config (per-repo takes precedence)
            config = PipelineConfiguration.ApplyBlacklistOverride(config, providerConfigs.FirstOrDefault(c => c.Id == repoProviderId));

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
                McpServers = profile.McpServers,
                ReviewerConfigs = resolvedReviewerConfigs
            };

            // Assign the job to the agent in the registry
            agent.ActiveJobId = run.RunId;
            _registry.TransitionStatus(agent.AgentId, AgentStatus.Busy);

            // Send the assignment via IAgentCommunication
            await _agentComm.AssignJobAsync(agent.ConnectionId, message, ct);

            _logger.Information(
                "Job {JobId} dispatched to agent {AgentId} for issue {IssueIdentifier} (profile={ProfileId}, qgcs={QgcCount}, reviewerConfigs={ReviewerConfigCount})",
                run.RunId, agent.AgentId, issueIdentifier, profile.Id, resolvedQgcs.Count, resolvedReviewerConfigs.Count);

            if (resolvedReviewerConfigs.Count > 0)
            {
                var reviewerSummary = string.Join(", ", resolvedReviewerConfigs.Select(r =>
                    $"{r.DisplayName} (labels: [{string.Join(", ", r.MatchLabels)}])"));
                _logger.Debug("Job {JobId} resolved reviewer configs: {ReviewerSummary}", run.RunId, reviewerSummary);
            }

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
            await _labelSwapper.SwapLabelAsync(issueProviderId, issueIdentifier, AgentLabels.Next, CancellationToken.None);

            return false;
        }
    }

    /// <summary>
    /// Dispatches a PR review job to a specific agent. Creates the PipelineRun with review metadata,
    /// pre-fetches linked issues, and sends the <see cref="JobAssignmentMessage"/> via SignalR.
    /// </summary>
    internal async Task<bool> DispatchReviewToAgentAsync(
        AgentEntry agent,
        string prIdentifier,
        string prBranchName,
        string prTitle,
        string? prDescription,
        string prUrl,
        string prTargetBranch,
        string issueProviderId,
        string repoProviderId,
        string? brainProviderId,
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
                _logger.Warning("No profile matches agent {AgentId} labels [{Labels}] for review dispatch", agent.AgentId, labelsStr);
                return false;
            }

            var agentProviderId = profile.AgentProviderConfigId;

            // Resolve reviewer configurations for this job (quality gates not needed for reviews)
            var allReviewerConfigs = await _configStore.LoadReviewerConfigsAsync(ct);
            var resolvedReviewerConfigs = _reviewerResolver.Resolve(allReviewerConfigs, requiredLabels);

            // Create the dispatched run via PipelineOrchestrationService
            var run = await _orchestration.CreateDispatchedRunAsync(
                issueProviderId, repoProviderId, prIdentifier,
                agentProviderId, agent.AgentId, ct,
                brainProviderId, pipelineProviderId: null, initiatedBy);

            if (run == null)
            {
                _logger.Warning("Failed to create dispatched run for PR review {PrIdentifier}", prIdentifier);
                return false;
            }

            // Populate review-specific metadata on the run
            // PipelineRun uses init-only properties for review fields, so we need to remove and re-create.
            // CreateDispatchedRunAsync creates the run without review fields — we replace it with a
            // fully-populated instance that includes RunType, ReviewPr*, and LinkedPullRequest.
            //
            // TODO: This remove-then-add pattern creates a brief window where IsIssueBeingProcessed(prIdentifier)
            // returns false. If a concurrent poll cycle checks eligibility during this gap, it could attempt
            // a duplicate dispatch for the same PR. In practice this is unlikely because:
            //   (a) the gap is sub-millisecond (no awaits between remove and add),
            //   (b) the label is swapped to agent:in-progress later in this method, which prevents re-polling.
            // The proper fix is to either make PipelineRun's review properties mutable (breaking the immutability
            // convention) or add an atomic UpdateRun/ReplaceRun method to OrchestratorRunService.
            _runService.RemoveRun(run.RunId);

            run = new PipelineRun
            {
                RunId = run.RunId,
                IssueIdentifier = prIdentifier,
                IssueTitle = prTitle,
                IssueProviderConfigId = issueProviderId,
                RepoProviderConfigId = repoProviderId,
                StartedAt = run.StartedAt,
                CurrentStep = PipelineStep.Created,
                RepositoryName = run.RepositoryName,
                ModelName = run.ModelName,
                BrainProviderConfigId = brainProviderId,
                PipelineProviderConfigId = null,
                InitiatedBy = initiatedBy,
                AgentId = agent.AgentId,
                AgentProviderConfigId = agentProviderId,
                RunType = PipelineRunType.Review,
                ReviewPrBranchName = prBranchName,
                ReviewPrTargetBranch = prTargetBranch,
                ReviewPrUrl = prUrl,
                ReviewPrDescription = prDescription,
                LinkedPullRequest = new LinkedPullRequest
                {
                    Number = int.TryParse(prIdentifier, out var prNum) ? prNum : 0,
                    BranchName = prBranchName,
                    Url = prUrl,
                    IsDraft = false
                }
            };

            // Pre-fetch linked issues (non-fatal on failure)
            var linkedIssueContexts = await PreFetchLinkedIssuesAsync(
                prIdentifier, issueProviderId, repoProviderId, ct);

            // Set LinkedIssueContexts on the run — requires re-creating since it's init-only
            if (linkedIssueContexts.Count > 0)
            {
                run = new PipelineRun
                {
                    RunId = run.RunId,
                    IssueIdentifier = prIdentifier,
                    IssueTitle = prTitle,
                    IssueProviderConfigId = issueProviderId,
                    RepoProviderConfigId = repoProviderId,
                    StartedAt = run.StartedAt,
                    CurrentStep = PipelineStep.Created,
                    RepositoryName = run.RepositoryName,
                    ModelName = run.ModelName,
                    BrainProviderConfigId = brainProviderId,
                    PipelineProviderConfigId = null,
                    InitiatedBy = initiatedBy,
                    AgentId = agent.AgentId,
                    AgentProviderConfigId = agentProviderId,
                    RunType = PipelineRunType.Review,
                    ReviewPrBranchName = prBranchName,
                    ReviewPrTargetBranch = prTargetBranch,
                    ReviewPrUrl = prUrl,
                    ReviewPrDescription = prDescription,
                    LinkedPullRequest = run.LinkedPullRequest,
                    LinkedIssueContexts = linkedIssueContexts
                };
            }

            // Re-register the run with review metadata
            _runService.AddRun(run);

            // Populate resolved profile and reviewer config IDs on the run
            run.ResolvedProfileId = profile.Id;
            run.ResolvedReviewerConfigIds = resolvedReviewerConfigs.Select(r => r.Id).ToList().AsReadOnly();

            // Build and prepare provider configs for the agent
            var providerConfigs = await PrepareProviderConfigsAsync(
                repoProviderId, agentProviderId, brainProviderId, pipelineProviderId: null, ct);

            var config = await _configStore.LoadPipelineConfigAsync(ct);

            // Override BrainReadOnly from the matching template (per-template setting)
            var matchingTemplate = config.PipelineJobTemplates.FirstOrDefault(t =>
                t.RepoProviderId == repoProviderId && t.BrainProviderId == brainProviderId);
            if (matchingTemplate is { BrainReadOnly: true })
                config = config with { BrainReadOnly = true };

            // Override blacklist settings from repo provider config (per-repo takes precedence)
            config = PipelineConfiguration.ApplyBlacklistOverride(config, providerConfigs.FirstOrDefault(c => c.Id == repoProviderId));

            // TODO: This label swap is redundant — CloneRepositoryStep also calls SwapAgentLabel(InProgress)
            // when the agent begins execution, resulting in a duplicate GitHub API call per review run.
            // We swap here (at dispatch time) so the PR is immediately marked in-progress before the agent
            // picks up the job, preventing the loop from re-dispatching it on the next cycle. CloneRepositoryStep
            // then swaps again because it doesn't know whether the orchestrator already did it.
            // To eliminate the duplication: either skip the swap in CloneRepositoryStep for agent-dispatched runs
            // (check if run.AgentId is set), or remove this swap and accept a brief window where the PR still
            // has agent:next (mitigated by IsIssueBeingProcessedOrQueued check in the loop).
            // Swap label to agent:in-progress before dispatch
            await _labelSwapper.SwapLabelAsync(
                repoProviderId, prIdentifier, AgentLabels.InProgress, LabelTargetKind.PullRequest, ct);

            // Build a synthetic IssueDetail and ParsedIssue from PR metadata for the job assignment
            var syntheticIssueDetail = new IssueDetail
            {
                Identifier = prIdentifier,
                Title = prTitle,
                Description = prDescription ?? string.Empty,
                Labels = Array.Empty<string>()
            };
            var syntheticParsedIssue = new IssueDescriptionParser().Parse(prDescription ?? string.Empty);

            var message = new JobAssignmentMessage
            {
                JobId = run.RunId,
                IssueIdentifier = prIdentifier,
                IssueDetail = syntheticIssueDetail,
                ParsedIssue = syntheticParsedIssue,
                IssueComments = Array.Empty<IssueComment>(),
                ExistingAnalysis = null,
                ForceRefreshAnalysis = false,
                LinkedPullRequest = run.LinkedPullRequest,
                RepoProviderConfigId = repoProviderId,
                AgentProviderConfigId = agentProviderId,
                BrainProviderConfigId = brainProviderId,
                PipelineProviderConfigId = null,
                ProviderConfigs = providerConfigs,
                PipelineConfiguration = config,
                InitiatedBy = initiatedBy,
                ResolvedProfileId = profile.Id,
                QualityGateConfigs = Array.Empty<QualityGateConfiguration>(),
                McpServers = profile.McpServers,
                ReviewerConfigs = resolvedReviewerConfigs,
                LinkedIssueContexts = linkedIssueContexts.Count > 0 ? linkedIssueContexts : null,
                RunType = PipelineRunType.Review,
                ReviewPrTargetBranch = prTargetBranch,
                ReviewPrDescription = prDescription
            };

            // Assign the job to the agent in the registry
            agent.ActiveJobId = run.RunId;
            _registry.TransitionStatus(agent.AgentId, AgentStatus.Busy);

            // Send the assignment via IAgentCommunication
            await _agentComm.AssignJobAsync(agent.ConnectionId, message, ct);

            _logger.Information(
                "Review job {JobId} dispatched to agent {AgentId} for PR {PrIdentifier} (profile={ProfileId}, reviewerConfigs={ReviewerConfigCount}, linkedIssues={LinkedIssueCount})",
                run.RunId, agent.AgentId, prIdentifier, profile.Id, resolvedReviewerConfigs.Count, linkedIssueContexts.Count);

            return true;
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Failed to dispatch review job to agent {AgentId} for PR {PrIdentifier}",
                agent.AgentId, prIdentifier);

            // Reset agent status on failure
            agent.ActiveJobId = null;
            _registry.TransitionStatus(agent.AgentId, AgentStatus.Idle);

            // Revert label on dispatch failure
            await _labelSwapper.SwapLabelAsync(
                repoProviderId, prIdentifier, AgentLabels.Next, LabelTargetKind.PullRequest, CancellationToken.None);

            return false;
        }
    }

    /// <summary>
    /// Pre-fetches linked issue details for a PR review dispatch.
    /// Calls <see cref="IRepositoryProvider.ExtractLinkedIssuesAsync"/> to get issue IDs,
    /// then fetches each issue's details via <see cref="IIssueProvider.GetIssueAsync"/>.
    /// Non-fatal: returns empty list on failure.
    /// </summary>
    private async Task<IReadOnlyList<LinkedIssueContext>> PreFetchLinkedIssuesAsync(
        string prIdentifier,
        string issueProviderId,
        string repoProviderId,
        CancellationToken ct)
    {
        var linkedIssueContexts = new List<LinkedIssueContext>();

        try
        {
            // Resolve repository provider to extract linked issues
            var repoConfigs = await _configStore.LoadProviderConfigsAsync(ProviderKind.Repository, ct);
            var repoConfig = repoConfigs.FirstOrDefault(c => c.Id == repoProviderId);
            if (repoConfig == null)
            {
                _logger.Warning("Repo provider config '{ConfigId}' not found for linked issue extraction", repoProviderId);
                return linkedIssueContexts.AsReadOnly();
            }

            IReadOnlyList<string> linkedIssueIds;
            await using (var repoProvider = _providerFactory.CreateRepositoryProvider(repoConfig))
            {
                if (!int.TryParse(prIdentifier, out var prNum))
                {
                    _logger.Warning("PR identifier '{PrIdentifier}' is not a valid integer, skipping linked issue extraction", prIdentifier);
                    return linkedIssueContexts.AsReadOnly();
                }

                linkedIssueIds = await repoProvider.ExtractLinkedIssuesAsync(prNum, ct);
            }

            if (linkedIssueIds.Count == 0)
            {
                _logger.Debug("No linked issues found for PR {PrIdentifier}", prIdentifier);
                return linkedIssueContexts.AsReadOnly();
            }

            // Resolve issue provider to fetch issue details
            var issueConfigs = await _configStore.LoadProviderConfigsAsync(ProviderKind.Issue, ct);
            var issueConfig = issueConfigs.FirstOrDefault(c => c.Id == issueProviderId);
            if (issueConfig == null)
            {
                _logger.Warning("Issue provider config '{ConfigId}' not found for linked issue pre-fetch", issueProviderId);
                return linkedIssueContexts.AsReadOnly();
            }

            await using (var issueProvider = _providerFactory.CreateIssueProvider(issueConfig))
            {
                foreach (var issueId in linkedIssueIds)
                {
                    try
                    {
                        var issueDetail = await issueProvider.GetIssueAsync(issueId, ct);
                        linkedIssueContexts.Add(new LinkedIssueContext
                        {
                            Identifier = issueId,
                            Title = issueDetail.Title,
                            Description = issueDetail.Description
                        });
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        _logger.Warning(ex, "Failed to fetch linked issue {IssueId} for PR {PrIdentifier}", issueId, prIdentifier);
                    }
                }
            }

            _logger.Information("Pre-fetched {Count} linked issue(s) for PR {PrIdentifier}", linkedIssueContexts.Count, prIdentifier);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.Warning(ex, "Failed to pre-fetch linked issues for PR {PrIdentifier}, continuing with empty context", prIdentifier);
        }

        return linkedIssueContexts.AsReadOnly();
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
        }

        // Swap label to agent:in-progress before dispatch (REQ-7.2)
        await _labelSwapper.SwapLabelAsync(issueProviderId, issueIdentifier, AgentLabels.InProgress, ct);

        // Detect existing analysis and rework state from comments
        string? existingAnalysis = null;
        bool forceRefreshAnalysis = false;
        var analysisComment = issueComments.FirstOrDefault(c => c.Body.Contains(CommentMarkers.AnalysisHeader));
        if (analysisComment is not null)
        {
            existingAnalysis = analysisComment.Body;
            var gateRejection = issueComments.FirstOrDefault(c => c.Body.Contains(CommentMarkers.GateRejection));
            var gateWontDo = issueComments.FirstOrDefault(c => c.Body.Contains(CommentMarkers.GateWontDo));
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
