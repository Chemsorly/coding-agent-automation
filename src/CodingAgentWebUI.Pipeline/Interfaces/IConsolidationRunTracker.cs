namespace CodingAgentWebUI.Pipeline.Interfaces;

/// <summary>
/// Narrow interface for transitioning consolidation runs to Running state.
/// Performs both the persistent store update AND the in-memory tracker update.
/// Separated from <see cref="IConsolidationService"/> to allow
/// <c>ConsolidationDispatcher</c> to call it without introducing a circular
/// dependency on the full <see cref="IConsolidationService"/>.
/// </summary>
public interface IConsolidationRunTracker
{
    /// <summary>
    /// Transitions a queued consolidation run to Running status.
    /// Updates the persistent store (Status, StartedAtUtc), the in-memory tracker
    /// (_runningRuns), invalidates the run history cache, and fires OnChange.
    /// No-op if the run is not found or not in Queued status.
    /// </summary>
    Task TransitionToRunningAsync(string runId, CancellationToken ct);
}
