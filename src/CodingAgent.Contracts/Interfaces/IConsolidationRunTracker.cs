using CodingAgent.Pipeline.Models;

namespace CodingAgent.Pipeline.Interfaces;

/// <summary>
/// Narrow interface for transitioning consolidation runs to Running state.
/// Performs both the persistent store update AND the in-memory tracker update.
/// Separated from <see cref="IConsolidationService"/> to allow
/// <c>ConsolidationDispatchService</c> to call it without introducing a circular
/// dependency on the full <see cref="IConsolidationService"/>.
/// </summary>
// TODO [WARNING]: Stale XML documentation below. The _runningRuns in-memory dictionary was removed
// in issue #3027. The summary and method doc still reference "_runningRuns" and "in-memory tracker",
// which no longer exist. Update to reflect that TransitionToRunningAsync now updates only the
// persistent store (and fires OnChange). (review-findings-dotnetspecialist.md)
public interface IConsolidationRunTracker
{
    /// <summary>
    /// Transitions a queued consolidation run to Running status.
    /// Updates the persistent store (Status, StartedAtUtc), the in-memory tracker
    /// (_runningRuns), invalidates the run history cache, and fires OnChange.
    /// No-op if the run is not found or not in Queued status.
    /// </summary>
    Task TransitionToRunningAsync(RunId runId, CancellationToken ct);
}
