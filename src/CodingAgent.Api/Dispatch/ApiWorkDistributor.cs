using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;
using Microsoft.Extensions.Logging;

namespace CodingAgent.Api.Dispatch;

/// <summary>
/// <see cref="IWorkDistributor"/> inside the Pipeline API (spec 051 2.3a). It enqueues and queries
/// work items through <see cref="WorkItemStore"/>, the same code the <c>/api/work-items</c> handlers
/// run, and reports the same outcomes as <c>KubernetesWorkDistributor</c>, which reaches those
/// handlers over HTTP. Dispatch logic that moves into the API (2.3c, 2.4a) uses this one.
/// </summary>
internal sealed class ApiWorkDistributor(
    WorkItemStore store,
    WorkItemStatusTransitionService transitions,
    ILogger<ApiWorkDistributor> logger) : IWorkDistributor
{
    /// <summary>
    /// Enqueues the request as a Pending work item; the Scheduler's dispatch loop creates the Job.
    /// A live item for the same issue counts as success (<c>AlreadyExists</c>), so a stale dedup
    /// snapshot does not revert the issue's label.
    /// </summary>
    public async Task<DistributionResult> DistributeAsync(JobDistributionRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        try
        {
            var (outcome, workItemId, reason) = await store.CreateAsync(request, ct);
            if (outcome == WorkItemCreateOutcome.Refused)
            {
                // Spec 053 DD2: no route, or one that cannot run. A configuration error, so not retried.
                return new DistributionResult(false, null, reason, IsPermanentFailure: true);
            }
            if (outcome == WorkItemCreateOutcome.LiveItemExists)
            {
                logger.LogInformation(
                    "A live WorkItem already exists for issue {IssueIdentifier}; treating it as already queued",
                    request.IssueIdentifier);
                return new DistributionResult(true, null, null, Queued: true, AlreadyExists: true);
            }

            logger.LogInformation("WorkItem {WorkItemId} enqueued as Pending for issue {IssueIdentifier}",
                workItemId, request.IssueIdentifier);
            return new DistributionResult(true, workItemId.ToString(), null, Queued: true);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Failed to enqueue WorkItem for issue {IssueIdentifier}", request.IssueIdentifier);
            return new DistributionResult(false, null, ex.Message);
        }
    }

    /// <summary>False when the work item does not exist or can no longer be cancelled.</summary>
    public async Task<bool> CancelJobAsync(JobId jobId, CancellationToken ct)
    {
        if (!Guid.TryParse(jobId.Value, out var workItemId))
            return false;

        var outcome = await transitions.TransitionAsync(
            workItemId,
            new WorkItemStatusRequest { Status = WorkItemStatus.Cancelled, ErrorMessage = "Cancelled by orchestrator" },
            ct);
        return outcome is StatusTransitionOutcome.Transitioned or StatusTransitionOutcome.AlreadyAtTarget;
    }

    public async Task<JobDistributionStatus> GetJobStatusAsync(JobId jobId, CancellationToken ct)
    {
        if (!Guid.TryParse(jobId.Value, out var workItemId))
            return JobDistributionStatus.Unknown;

        return await store.GetStatusAsync(workItemId, ct) switch
        {
            WorkItemStatus.Pending => JobDistributionStatus.Pending,
            WorkItemStatus.Dispatched => JobDistributionStatus.Dispatched,
            WorkItemStatus.Running => JobDistributionStatus.Running,
            WorkItemStatus.Succeeded => JobDistributionStatus.Succeeded,
            WorkItemStatus.Failed => JobDistributionStatus.Failed,
            WorkItemStatus.Cancelled => JobDistributionStatus.Cancelled,
            _ => JobDistributionStatus.Unknown,
        };
    }

    public Task<bool> IsIssueDistributedAsync(IssueIdentifier issueIdentifier, ProviderConfigId issueProviderConfigId, CancellationToken ct) =>
        store.IsDistributedAsync(issueIdentifier.Value, issueProviderConfigId.Value, ct);

    public async Task<HashSet<(IssueIdentifier IssueIdentifier, ProviderConfigId IssueProviderConfigId)>> GetActiveIssueIdentifiersAsync(CancellationToken ct) =>
        (await store.GetActiveIdentifiersAsync(ct))
            .Select(p => ((IssueIdentifier)p.IssueIdentifier, (ProviderConfigId)p.IssueProviderConfigId))
            .ToHashSet();

    public Task<int> GetActiveDecompositionCountAsync(CancellationToken ct) => store.GetActiveDecompositionCountAsync(ct);
}
