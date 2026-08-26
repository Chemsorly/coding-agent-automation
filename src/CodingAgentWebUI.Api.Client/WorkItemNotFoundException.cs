namespace CodingAgentWebUI.Api.Client;

/// <summary>
/// Thrown by <see cref="PipelineApiWorkItemClient.ClaimAsync"/> when the Pipeline API returns
/// HTTP 404 during a claim attempt. This indicates the work item was present in the pending
/// list but no longer exists — either deleted between <c>GetPendingAsync</c> and
/// <c>ClaimAsync</c> (data race) or missing due to a bug in the pending query.
/// Callers should log a warning and skip the item rather than treating it as a normal
/// HTTP 409 contention case.
/// </summary>
public sealed class WorkItemNotFoundException : Exception
{
    public Guid WorkItemId { get; }

    public WorkItemNotFoundException(Guid workItemId)
        : base($"WorkItem {workItemId} not found during claim (HTTP 404). Item may have been deleted between GetPendingAsync and ClaimAsync.")
    {
        WorkItemId = workItemId;
    }
}
