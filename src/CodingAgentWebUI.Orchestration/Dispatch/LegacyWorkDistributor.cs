using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using ILogger = Serilog.ILogger;

namespace CodingAgentWebUI.Orchestration.Dispatch;

/// <summary>
/// No-DB mode adapter: satisfies <see cref="IWorkDistributor"/> by delegating to the
/// existing <see cref="AgentJobDispatcher"/> (in-memory queue + SignalR push).
/// Does NOT insert WorkItem rows — operates purely in-memory, identical to current behavior.
/// Registered when <c>Database:ConnectionString</c> is not configured.
/// </summary>
public sealed class LegacyWorkDistributor : IWorkDistributor
{
    private readonly IJobDispatcher _jobDispatcher;
    private readonly JobDispatcherService _dispatcherService;
    private readonly IOrchestratorRunService _runService;
    private readonly ILogger _logger;

    /// <summary>
    /// Exposes the internal <see cref="AgentJobDispatcher"/> for same-assembly consumers
    /// (e.g., <see cref="JobQueueDrainService"/>) that need direct dispatch access.
    /// </summary>
    internal IJobDispatcher JobDispatcher => _jobDispatcher;

    internal LegacyWorkDistributor(
        IJobDispatcher jobDispatcher,
        JobDispatcherService dispatcherService,
        IOrchestratorRunService runService,
        ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(jobDispatcher);
        ArgumentNullException.ThrowIfNull(dispatcherService);
        ArgumentNullException.ThrowIfNull(runService);
        ArgumentNullException.ThrowIfNull(logger);

        _jobDispatcher = jobDispatcher;
        _dispatcherService = dispatcherService;
        _runService = runService;
        _logger = logger;
    }

    /// <inheritdoc />
    /// <remarks>
    /// Delegates to the existing <see cref="AgentJobDispatcher.TryDispatchAsync"/> (implementation),
    /// <see cref="AgentJobDispatcher.TryDispatchReviewAsync"/> (review), or
    /// <see cref="AgentJobDispatcher.TryDispatchDecompositionAsync"/> (decomposition).
    /// All orchestration logic (issue fetch, label swap, run creation, profile resolution)
    /// stays inside <see cref="AgentJobDispatcher"/> — no extraction needed for backward compat.
    /// </remarks>
    public async Task<DistributionResult> DistributeAsync(JobDistributionRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        bool success;

        switch (request.TaskType)
        {
            case WorkItemTaskType.Review:
                var reviewRequest = new ReviewDispatchRequest
                {
                    PrIdentifier = request.IssueIdentifier,
                    PrBranchName = request.LinkedPullRequest?.BranchName ?? "",
                    PrTitle = request.IssueDetail?.Title ?? "",
                    PrUrl = request.LinkedPullRequest?.Url ?? "",
                    PrTargetBranch = request.ReviewPrTargetBranch ?? "",
                    PrDescription = request.ReviewPrDescription,
                    PrAuthor = request.ReviewPrAuthor,
                    IssueProviderId = request.IssueProviderConfigId,
                    RepoProviderId = request.RepoProviderConfigId,
                    BrainProviderId = request.BrainProviderConfigId,
                    InitiatedBy = request.InitiatedBy
                };
                success = await _jobDispatcher.TryDispatchReviewAsync(reviewRequest, ct);
                break;

            case WorkItemTaskType.Decomposition:
                success = await _jobDispatcher.TryDispatchDecompositionAsync(
                    request.IssueIdentifier,
                    request.IssueDetail?.Title ?? "",
                    request.RunType,
                    request.IssueProviderConfigId,
                    request.RepoProviderConfigId,
                    request.BrainProviderConfigId,
                    request.InitiatedBy,
                    ct,
                    request.DecompositionSource);
                break;

            default: // Implementation, Consolidation
                success = await _jobDispatcher.TryDispatchAsync(
                    request.IssueIdentifier,
                    request.IssueProviderConfigId,
                    request.RepoProviderConfigId,
                    request.BrainProviderConfigId,
                    request.PipelineProviderConfigId,
                    request.InitiatedBy,
                    ct,
                    request.IssueDetail?.Title);
                break;
        }

        return new DistributionResult(success, null, success ? null : "No agent available");
    }

    /// <inheritdoc />
    /// <remarks>Not supported in legacy mode — agents cannot be cancelled externally.</remarks>
    public Task<bool> CancelJobAsync(string jobId, CancellationToken ct)
        => Task.FromResult(false);

    /// <inheritdoc />
    /// <remarks>No persistent state in legacy mode.</remarks>
    public Task<JobDistributionStatus> GetJobStatusAsync(string jobId, CancellationToken ct)
        => Task.FromResult(JobDistributionStatus.Unknown);

    /// <inheritdoc />
    public Task<bool> IsIssueDistributedAsync(string issueIdentifier, string issueProviderConfigId, CancellationToken ct)
        => Task.FromResult(_jobDispatcher.IsIssueBeingProcessedOrQueued(issueIdentifier, issueProviderConfigId));

    /// <inheritdoc />
    /// <remarks>
    /// Combines in-memory state from <see cref="JobDispatcherService"/> (queued issues)
    /// and <see cref="IOrchestratorRunService"/> (actively running issues).
    /// </remarks>
    public Task<HashSet<(string IssueIdentifier, string IssueProviderConfigId)>> GetActiveIssueIdentifiersAsync(CancellationToken ct)
    {
        var result = new HashSet<(string, string)>();

        // Add identifiers from queued jobs
        foreach (var job in _dispatcherService.GetQueuedJobs())
        {
            result.Add((job.IssueIdentifier, job.IssueProviderId));
        }

        // Add identifiers from active runs
        foreach (var run in _runService.GetActiveRuns())
        {
            result.Add((run.IssueIdentifier, run.IssueProviderConfigId));
        }

        return Task.FromResult(result);
    }
}
