namespace CodingAgent.Orchestration.Dispatch;

/// <summary>
/// Minimal role interface for looking up the K8s Job name associated with a WorkItem.
/// </summary>
public interface IK8sJobNameLookup
{
    /// <summary>Returns the K8s Job name set on a WorkItem, or null if the WorkItem does not exist or has no job name.</summary>
    Task<string?> GetK8sJobNameAsync(Guid workItemId, CancellationToken ct = default);
}
