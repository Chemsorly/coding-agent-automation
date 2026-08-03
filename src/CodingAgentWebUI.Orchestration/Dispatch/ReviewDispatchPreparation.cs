using CodingAgentWebUI.Orchestration.Registry;
using CodingAgentWebUI.Pipeline;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using ILogger = Serilog.ILogger;

namespace CodingAgentWebUI.Orchestration.Dispatch;

/// <summary>
/// Strategy for preparing a PR review dispatch.
/// Resolves reviewer configs, reserves a run, pre-fetches linked issues, and builds synthetic issue context.
/// </summary>
internal sealed class ReviewDispatchPreparation : IDispatchPreparationHandler
{
    private readonly DispatchInfrastructure _infra;
    private readonly IDispatchRunCreator _orchestration;
    private readonly ILogger _logger;
    private readonly AgentEntry _agent;
    private readonly ReviewDispatchRequest _request;
    private readonly IReadOnlyList<string> _requiredLabels;

    public ReviewDispatchPreparation(
        DispatchInfrastructure infra,
        IDispatchRunCreator orchestration,
        ILogger logger,
        AgentEntry agent,
        ReviewDispatchRequest request,
        IReadOnlyList<string> requiredLabels)
    {
        _infra = infra;
        _orchestration = orchestration;
        _logger = logger;
        _agent = agent;
        _request = request;
        _requiredLabels = requiredLabels;
    }

    public async Task<AgentJobDispatcher.DispatchPipelineResult?> PrepareAsync(
        PipelineProject project,
        AgentProfile profile,
        string agentProviderId,
        CancellationToken ct)
    {
        // Resolve reviewer configurations for this job (quality gates not needed for reviews)
        var resolvedReviewerConfigs = await _infra.Resolution.ResolveReviewersAsync(_requiredLabels, ct);

        // Reserve a run ID and dedup guard via PipelineOrchestrationService
        var reservation = await _orchestration.ReserveRunIdAsync(
            new DispatchRunRequest
            {
                IssueProviderId = _request.IssueProviderId,
                RepoProviderId = _request.RepoProviderId,
                IssueIdentifier = _request.PrIdentifier,
                AgentProviderId = agentProviderId,
                AgentId = _agent.AgentId,
                BrainProviderId = _request.BrainProviderId,
                PipelineProviderId = null,
                InitiatedBy = _request.InitiatedBy
            }, ct);

        if (reservation == null)
        {
            _logger.Warning("Failed to reserve run for PR review {PrIdentifier}", _request.PrIdentifier);
            return null;
        }

        // Pre-fetch linked issues before constructing the final run (non-fatal on failure)
        var linkedIssueContexts = await PreFetchLinkedIssuesAsync(
            _request.PrIdentifier, _request.IssueProviderId, _request.RepoProviderId, ct);

        // Construct the fully-populated review run using reserved metadata
        var run = PipelineRun.CreateReview(
            runId: reservation.RunId,
            issueIdentifier: _request.PrIdentifier,
            issueTitle: _request.PrTitle,
            issueProviderConfigId: _request.IssueProviderId,
            repoProviderConfigId: _request.RepoProviderId,
            reviewPrBranchName: _request.PrBranchName,
            reviewPrTargetBranch: _request.PrTargetBranch,
            startedAt: reservation.StartedAt,
            initiatedBy: _request.InitiatedBy,
            agentId: _agent.AgentId,
            agentProviderConfigId: agentProviderId,
            brainProviderConfigId: _request.BrainProviderId,
            reviewPrUrl: _request.PrUrl,
            reviewPrDescription: _request.PrDescription,
            reviewPrAuthor: _request.PrAuthor,
            linkedIssueContexts: linkedIssueContexts.Count > 0 ? linkedIssueContexts : null);
        run.RepositoryName = reservation.RepositoryName;
        run.ModelName = reservation.ModelName;
        run.LinkedPullRequest = new LinkedPullRequest
        {
            Number = int.TryParse(_request.PrIdentifier, out var prNum) ? prNum : 0,
            BranchName = _request.PrBranchName,
            Url = _request.PrUrl,
            IsDraft = false
        };

        // Atomically replace the sentinel with the fully-populated run
        // Note: ApplyRunMetadata is called by the template after this delegate returns
        _orchestration.RegisterDispatchedRun(run);

        // Populate resolved reviewer config IDs on the run
        run.ResolvedReviewerConfigIds = resolvedReviewerConfigs.Select(r => r.Id).ToList().AsReadOnly();

        // Build and prepare provider configs for the agent
        // Settings resolution: Global → Project overrides → Template overrides (blacklist from ProviderConfig)
        var (providerConfigs, config) = await _infra.PrepareAndResolveConfigAsync(
            _request.RepoProviderId, agentProviderId, _request.BrainProviderId, null, project, _logger, ct);

        // Build a synthetic IssueDetail and ParsedIssue from PR metadata for the job assignment
        var (syntheticIssueDetail, syntheticParsedIssue) = DispatchInfrastructure.BuildSyntheticIssueContext(
            _request.PrIdentifier, _request.PrTitle, _request.PrDescription);

        var pipelineCtx = new AgentJobDispatcher.DispatchPipelineContext
        {
            Agent = _agent,
            Run = run,
            Profile = profile,
            IssueIdentifier = _request.PrIdentifier,
            IssueDetail = syntheticIssueDetail,
            ParsedIssue = syntheticParsedIssue,
            IssueComments = Array.Empty<IssueComment>(),
            RepoProviderId = _request.RepoProviderId,
            AgentProviderId = agentProviderId,
            BrainProviderId = _request.BrainProviderId,
            PipelineProviderId = null,
            IssueProviderId = _request.IssueProviderId,
            ProviderConfigs = providerConfigs,
            Config = config,
            InitiatedBy = _request.InitiatedBy,
            Project = project
        };

        Func<JobAssignmentMessage, JobAssignmentMessage> customize = msg => msg with
        {
            LinkedPullRequest = run.LinkedPullRequest,
            LinkedIssueContexts = linkedIssueContexts.Count > 0 ? linkedIssueContexts : null,
            RunType = PipelineRunType.Review,
            ReviewPrTargetBranch = _request.PrTargetBranch,
            ReviewPrDescription = _request.PrDescription,
            ReviewPrAuthor = _request.PrAuthor,
            ReviewerConfigs = resolvedReviewerConfigs
        };

        Action onSuccess = () =>
        {
            _logger.Information(
                "Review job {JobId} dispatched to agent {AgentId} for PR {PrIdentifier} (profile={ProfileId}, reviewerConfigs={ReviewerConfigCount}, linkedIssues={LinkedIssueCount})",
                run.RunId, _agent.AgentId, _request.PrIdentifier, profile.Id, resolvedReviewerConfigs.Count, linkedIssueContexts.Count);
        };

        return new AgentJobDispatcher.DispatchPipelineResult(pipelineCtx, customize, onSuccess);
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
            var repoConfig = await _infra.Resolution.ConfigStore.GetProviderConfigByIdAsync(repoProviderId, ProviderKind.Repository, ct);
            if (repoConfig == null)
            {
                _logger.Warning("Repo provider config '{ConfigId}' not found for linked issue extraction", repoProviderId);
                return linkedIssueContexts.AsReadOnly();
            }

            IReadOnlyList<string> linkedIssueIds;
            await using (var repoProvider = _infra.ProviderFactory.CreateRepositoryProvider(repoConfig))
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
            var issueConfig = await _infra.Resolution.ConfigStore.GetProviderConfigByIdAsync(issueProviderId, ProviderKind.Issue, ct);
            if (issueConfig == null)
            {
                _logger.Warning("Issue provider config '{ConfigId}' not found for linked issue pre-fetch", issueProviderId);
                return linkedIssueContexts.AsReadOnly();
            }

            await using (var issueProvider = _infra.ProviderFactory.CreateIssueProvider(issueConfig))
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
}