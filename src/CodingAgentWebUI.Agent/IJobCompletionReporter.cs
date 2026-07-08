using CodingAgentWebUI.Pipeline.Models;

namespace CodingAgentWebUI.Agent;

/// <summary>
/// Unified interface for reporting job completion to the orchestrator.
/// Ensures consistent completion durability across both agent execution modes:
/// <list type="bullet">
///   <item><see cref="SignalRCompletionReporter"/> — SignalR with Polly resilience + CriticalMessageBuffer (SignalR mode)</item>
///   <item><see cref="HttpPrimaryCompletionReporter"/> — HTTP POST (primary, durable) + SignalR (secondary, real-time) (K8s mode)</item>
/// </list>
/// </summary>
public interface IJobCompletionReporter
{
    /// <summary>
    /// Reports job completion to the orchestrator. The delivery mechanism and durability
    /// guarantees depend on the implementation.
    /// </summary>
    /// <param name="jobId">The job ID to report completion for.</param>
    /// <param name="payload">The completion payload with final step, timing, and result metadata.</param>
    /// <param name="ct">Cancellation token.</param>
    Task ReportCompletionAsync(string jobId, JobCompletionPayload payload, CancellationToken ct);
}
