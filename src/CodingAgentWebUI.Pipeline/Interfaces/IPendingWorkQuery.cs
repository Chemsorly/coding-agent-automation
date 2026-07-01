using CodingAgentWebUI.Pipeline.Models;

namespace CodingAgentWebUI.Pipeline.Interfaces;

/// <summary>
/// Provides a unified view of work items queued for dispatch, regardless of the
/// underlying distribution mode (Legacy in-memory, DB+SignalR, or Kubernetes).
/// Consumed by UI components and telemetry — never by dispatch logic.
/// </summary>
public interface IPendingWorkQuery
{
    /// <summary>Returns all pending jobs ordered FIFO (oldest first).</summary>
    Task<IReadOnlyList<PendingJob>> GetPendingJobsAsync(CancellationToken ct = default);

    /// <summary>Current count of pending jobs (non-async for telemetry gauges).</summary>
    int PendingCount { get; }
}
