using CodingAgent.Pipeline.Models;

namespace CodingAgent.Pipeline.Interfaces;

/// <summary>
/// Spec 056 DD11: handles an agent's report that its credential could not be used (quota used up or login rejected).
/// Every completion path (HTTP status, hub job completion, hub consolidation completion) calls it first and returns
/// its outcome without falling through.
/// </summary>
public interface IProviderFailureHandler
{
    /// <summary>
    /// Ends work item <paramref name="workItemId"/> (Failed, <c>ProviderUnavailable</c>), pauses its credential and
    /// queues the work again as a new Pending work item — exactly once, for the agent that runs it
    /// (<paramref name="reporter"/>, its Job name or agent id). Returns false when another path already handled it, the
    /// item is no longer running, or <paramref name="reporter"/> does not own it; the caller then does nothing more.
    /// </summary>
    Task<bool> HandleAsync(Guid workItemId, string reporter, AgentProviderFailure failure, CancellationToken ct);
}
