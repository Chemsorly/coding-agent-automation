using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Pipeline.Services;

namespace CodingAgentWebUI.Orchestration.Dispatch;

public sealed partial class AgentJobDispatcher
{
    /// <inheritdoc />
    public async Task<bool> TryDispatchAsync(
        string issueIdentifier,
        string issueProviderId,
        string repoProviderId,
        string? brainProviderId,
        string? pipelineProviderId,
        string initiatedBy,
        CancellationToken ct,
        string? issueTitle = null,
        PipelineProject? project = null)
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
        var repoConfig = await _configStore.GetProviderConfigByIdAsync(repoProviderId, ProviderKind.Repository, ct);
        var requiredLabels = JobDispatcherService.ResolveRequiredLabels(repoConfig, config);

        // Try to find an idle agent
        var agent = _dispatcher.SelectAgent(requiredLabels);

        if (agent != null)
        {
            // Agent available — dispatch immediately
            return await DispatchToAgentAsync(
                agent, issueIdentifier, issueProviderId, repoProviderId,
                brainProviderId, pipelineProviderId, initiatedBy, requiredLabels, ct,
                project: project);
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
            RequiredLabels = requiredLabels,
            Project = project
        });

        if (enqueued)
            _logger.Information("Issue {IssueIdentifier} enqueued for dispatch (no idle agents)", issueIdentifier);

        return enqueued;
    }

    /// <inheritdoc />
    public async Task<bool> TryDispatchReviewAsync(ReviewDispatchRequest request, CancellationToken ct, PipelineProject? project = null)
    {
        ArgumentNullException.ThrowIfNull(request);

        // Check if already being processed
        if (IsIssueBeingProcessedOrQueued(request.PrIdentifier))
        {
            _logger.Information("PR {PrIdentifier} already being processed or queued, skipping review dispatch", request.PrIdentifier);
            return false;
        }

        // Resolve required labels for agent matching
        var config = await _configStore.LoadPipelineConfigAsync(ct);
        var repoConfig = await _configStore.GetProviderConfigByIdAsync(request.RepoProviderId, ProviderKind.Repository, ct);
        var requiredLabels = JobDispatcherService.ResolveRequiredLabels(repoConfig, config);

        // Try to find an idle agent
        var agent = _dispatcher.SelectAgent(requiredLabels);

        if (agent != null)
        {
            // Agent available — dispatch immediately
            return await DispatchReviewToAgentAsync(
                agent, request, requiredLabels, ct, project: project);
        }

        // No idle agent — enqueue for later dispatch
        var enqueued = _dispatcher.EnqueueJob(new PendingJob
        {
            IssueIdentifier = request.PrIdentifier,
            IssueTitle = request.PrTitle,
            IssueProviderId = request.IssueProviderId,
            RepoProviderId = request.RepoProviderId,
            BrainProviderId = request.BrainProviderId,
            PipelineProviderId = null,
            EnqueuedAt = DateTimeOffset.UtcNow,
            InitiatedBy = request.InitiatedBy,
            RequiredLabels = requiredLabels,
            RunType = PipelineRunType.Review,
            PrBranchName = request.PrBranchName,
            PrDescription = request.PrDescription,
            PrAuthor = request.PrAuthor,
            PrUrl = request.PrUrl,
            PrTargetBranch = request.PrTargetBranch,
            Project = project
        });

        if (enqueued)
            _logger.Information("PR review {PrIdentifier} enqueued for dispatch (no idle agents)", request.PrIdentifier);

        return enqueued;
    }

    /// <inheritdoc />
    public async Task<bool> TryDispatchDecompositionAsync(
        string epicIdentifier,
        string epicTitle,
        PipelineRunType phaseType,
        string issueProviderId,
        string repoProviderId,
        string? brainProviderId,
        string initiatedBy,
        CancellationToken ct,
        string? decompositionSource = null,
        PipelineProject? project = null)
    {
        ArgumentNullException.ThrowIfNull(epicIdentifier);
        ArgumentNullException.ThrowIfNull(epicTitle);
        ArgumentNullException.ThrowIfNull(issueProviderId);
        ArgumentNullException.ThrowIfNull(repoProviderId);
        ArgumentNullException.ThrowIfNull(initiatedBy);

        if (phaseType is not (PipelineRunType.DecompositionAnalysis or PipelineRunType.Decomposition))
            throw new ArgumentOutOfRangeException(nameof(phaseType), phaseType, "Must be DecompositionAnalysis or Decomposition");

        // Check if already being processed
        if (_orchestration.IsIssueBeingProcessed(epicIdentifier) || _dispatcher.IsIssueQueued(epicIdentifier))
        {
            _logger.Information("Epic {EpicIdentifier} already being processed or queued, skipping decomposition dispatch", epicIdentifier);
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
            return await DispatchDecompositionToAgentAsync(
                agent, epicIdentifier, epicTitle, phaseType,
                issueProviderId, repoProviderId, brainProviderId,
                initiatedBy, requiredLabels, ct,
                decompositionSource: decompositionSource,
                project: project);
        }

        // No idle agent — enqueue for later dispatch
        var enqueued = _dispatcher.EnqueueJob(new PendingJob
        {
            IssueIdentifier = epicIdentifier,
            IssueTitle = epicTitle,
            IssueProviderId = issueProviderId,
            RepoProviderId = repoProviderId,
            BrainProviderId = brainProviderId,
            PipelineProviderId = null,
            EnqueuedAt = DateTimeOffset.UtcNow,
            InitiatedBy = initiatedBy,
            RequiredLabels = requiredLabels,
            RunType = phaseType,
            DecompositionSource = decompositionSource,
            Project = project
        });

        if (enqueued)
            _logger.Information("Decomposition {Phase} for epic {EpicIdentifier} enqueued for dispatch (no idle agents)", phaseType, epicIdentifier);

        return enqueued;
    }
}
