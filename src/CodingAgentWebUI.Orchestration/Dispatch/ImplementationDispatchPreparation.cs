using CodingAgentWebUI.Orchestration.Registry;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using ILogger = Serilog.ILogger;

namespace CodingAgentWebUI.Orchestration.Dispatch;

/// <summary>
/// Strategy for preparing an implementation dispatch.
/// Performs QG/reviewer resolution, issue context fetching, run creation, and staleness detection.
/// </summary>
internal sealed class ImplementationDispatchPreparation : IDispatchPreparationHandler
{
    private readonly DispatchInfrastructure _infra;
    private readonly IDispatchRunCreator _orchestration;
    private readonly ILogger _logger;
    private readonly AgentEntry _agent;
    private readonly string _issueIdentifier;
    private readonly ProviderConfigId _issueProviderId;
    private readonly ProviderConfigId _repoProviderId;
    private readonly string? _brainProviderId;
    private readonly string? _pipelineProviderId;
    private readonly string _initiatedBy;
    private readonly IReadOnlyList<string> _requiredLabels;

    public ImplementationDispatchPreparation(ImplementationDispatchRequest request)
    {
        _infra = request.Infra;
        _orchestration = request.Orchestration;
        _logger = request.Logger;
        _agent = request.Agent;
        _issueIdentifier = request.IssueIdentifier;
        _issueProviderId = request.IssueProviderId;
        _repoProviderId = request.RepoProviderId;
        _brainProviderId = request.BrainProviderId;
        _pipelineProviderId = request.PipelineProviderId;
        _initiatedBy = request.InitiatedBy;
        _requiredLabels = request.RequiredLabels;
    }

    public async Task<AgentJobDispatcher.DispatchPipelineResult?> PrepareAsync(
        PipelineProject project,
        AgentProfile profile,
        string agentProviderId,
        CancellationToken ct)
    {
        // Shared dispatch preparation: QG/reviewer resolution, issue context, config, staleness
        var preparation = await _infra.PrepareDispatchCoreAsync(
            new DispatchCoreRequest(
                _requiredLabels, _issueIdentifier, _issueProviderId,
                _repoProviderId, agentProviderId, _brainProviderId, _pipelineProviderId,
                project, _logger),
            ct);
        if (preparation is null) return null;

        var (resolvedQgcs, resolvedReviewerConfigs, issueContext, providerConfigs, config,
            forceRefresh, stalenessSignal, refreshCount) = preparation.Value;

        // Create the dispatched run via PipelineOrchestrationService
        var run = await _orchestration.CreateDispatchedRunAsync(
            new DispatchRunRequest
            {
                IssueProviderId = _issueProviderId,
                RepoProviderId = _repoProviderId,
                IssueIdentifier = _issueIdentifier,
                AgentProviderId = agentProviderId,
                AgentId = _agent.AgentId.Value,
                BrainProviderId = _brainProviderId,
                PipelineProviderId = _pipelineProviderId,
                InitiatedBy = _initiatedBy
            }, ct);

        if (run == null)
        {
            _logger.Warning("Failed to create dispatched run for issue {IssueIdentifier}", _issueIdentifier);
            return null;
        }

        // Set resolved metadata on the run (ApplyRunMetadata is called by the template)
        run.ResolvedQualityGateConfigIds = resolvedQgcs.Select(q => q.Id).ToList().AsReadOnly();
        run.ResolvedReviewerConfigIds = resolvedReviewerConfigs.Select(r => r.Id).ToList().AsReadOnly();
        run.IssueTitle = issueContext.IssueDetail.Title;

        var pipelineCtx = new AgentJobDispatcher.DispatchPipelineContext
        {
            Agent = _agent,
            Run = run,
            Profile = profile,
            IssueIdentifier = _issueIdentifier,
            IssueDetail = issueContext.IssueDetail,
            ParsedIssue = issueContext.ParsedIssue,
            IssueComments = issueContext.IssueComments,
            RepoProviderId = _repoProviderId,
            AgentProviderId = agentProviderId,
            BrainProviderId = _brainProviderId,
            PipelineProviderId = _pipelineProviderId,
            IssueProviderId = _issueProviderId,
            ProviderConfigs = providerConfigs,
            Config = config,
            InitiatedBy = _initiatedBy,
            Project = project
        };

        Func<JobAssignmentMessage, JobAssignmentMessage> customize = msg => msg with
        {
            ExistingAnalysis = issueContext.ExistingAnalysis,
            ForceRefreshAnalysis = forceRefresh,
            StalenessSignal = stalenessSignal,
            AnalysisRefreshCount = refreshCount,
            QualityGateConfigs = resolvedQgcs,
            ReviewerConfigs = resolvedReviewerConfigs
        };

        Action onSuccess = () =>
        {
            _logger.Information(
                "Job {JobId} dispatched to agent {AgentId} for issue {IssueIdentifier} (profile={ProfileId}, qgcs={QgcCount}, reviewerConfigs={ReviewerConfigCount}, project={ProjectName})",
                run.RunId, _agent.AgentId, _issueIdentifier, profile.Id, resolvedQgcs.Count, resolvedReviewerConfigs.Count, project.Name);

            if (resolvedReviewerConfigs.Count > 0)
            {
                var reviewerSummary = string.Join(", ", resolvedReviewerConfigs.Select(r =>
                    $"{r.DisplayName} (labels: [{string.Join(", ", r.MatchLabels)}])"));
                _logger.Debug("Job {JobId} resolved reviewer configs: {ReviewerSummary}", run.RunId, reviewerSummary);
            }
        };

        return new AgentJobDispatcher.DispatchPipelineResult(pipelineCtx, customize, onSuccess);
    }
}