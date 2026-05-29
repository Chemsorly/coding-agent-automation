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
        var repoConfig = await _configStore.GetProviderConfigByIdAsync(repoProviderId, ProviderKind.Repository, ct);
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
        var repoConfig = await _configStore.GetProviderConfigByIdAsync(repoProviderId, ProviderKind.Repository, ct);
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

    /// <inheritdoc />
    public async Task<bool> TryDispatchDecompositionAsync(
        string epicIdentifier,
        string epicTitle,
        PipelineRunType phaseType,
        string issueProviderId,
        string repoProviderId,
        string? brainProviderId,
        string initiatedBy,
        CancellationToken ct)
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
                initiatedBy, requiredLabels, ct);
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
            RunType = phaseType
        });

        if (enqueued)
            _logger.Information("Decomposition {Phase} for epic {EpicIdentifier} enqueued for dispatch (no idle agents)", phaseType, epicIdentifier);

        return enqueued;
    }
}
