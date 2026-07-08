using CodingAgentWebUI.Pipeline.Models;

namespace CodingAgentWebUI.Agent;

/// <summary>
/// HTTP lifecycle client for the orchestrator's Work Item API endpoints.
/// Used in K8s mode by <see cref="WorkItemAgentService"/> to fetch assignments and report
/// status transitions, enabling full lifecycle testing without HTTP.
/// </summary>
/// <remarks>
/// <para>Implemented by <see cref="WorkItemHttpClient"/> which performs actual HTTP calls
/// with resilience (retries, circuit breaker, timeouts) via the standard resilience handler.</para>
/// </remarks>
public interface IWorkItemLifecycleClient
{
    /// <summary>
    /// Fetches the work item assignment from the orchestrator.
    /// </summary>
    /// <returns>
    /// The deserialized <see cref="JobAssignmentMessage"/>, or null if the work item
    /// is in a terminal status (410 Gone).
    /// </returns>
    /// <exception cref="WorkItemFetchException">Thrown when a non-retryable error occurs or all retries are exhausted.</exception>
    Task<JobAssignmentMessage?> GetAssignmentAsync(string workItemId, CancellationToken ct);

    /// <summary>
    /// Posts a status transition to the orchestrator.
    /// </summary>
    /// <returns>True if the transition was accepted (200); false if rejected (400) or not found (404).</returns>
    /// <exception cref="WorkItemStatusPostException">Thrown when all retries are exhausted.</exception>
    Task<bool> PostStatusAsync(string workItemId, WorkItemStatusUpdate update, CancellationToken ct);
}
