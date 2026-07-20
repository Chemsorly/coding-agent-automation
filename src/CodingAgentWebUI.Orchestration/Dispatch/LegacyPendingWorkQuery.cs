using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;

namespace CodingAgentWebUI.Orchestration.Dispatch;

/// <summary>
/// Legacy-mode implementation of <see cref="IPendingWorkQuery"/>.
/// Delegates to the in-memory <see cref="JobDeduplicationGuardService"/> queue.
/// </summary>
public sealed class LegacyPendingWorkQuery : IPendingWorkQuery
{
    private readonly JobDeduplicationGuardService _dispatcher;

    public LegacyPendingWorkQuery(JobDeduplicationGuardService dispatcher)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);
        _dispatcher = dispatcher;
    }

    public int PendingCount => _dispatcher.QueueLength;

    public Task<IReadOnlyList<PendingJob>> GetPendingJobsAsync(CancellationToken ct = default)
        => Task.FromResult(_dispatcher.GetQueuedJobs());
}
