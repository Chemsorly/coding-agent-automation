using CodingAgentWebUI.Pipeline.Models;

namespace CodingAgentWebUI.Pipeline.Interfaces;

/// <summary>
/// Manages feedback data caching for consolidation runs.
/// Prepares, stores, retrieves, and clears feedback data used during harness suggestion dispatch.
/// </summary>
public interface IConsolidationFeedbackCache
{
    /// <summary>
    /// Prepares RunFeedback data from pipeline run history for harness suggestion analysis.
    /// Filters to only feedback collected since the last successful harness suggestion run.
    /// </summary>
    /// <param name="run">The consolidation run to prepare feedback for.</param>
    /// <param name="ct">Cancellation token.</param>
    Task PrepareFeedbackDataAsync(ConsolidationRun run, CancellationToken ct);

    /// <summary>
    /// Gets the cached feedback data JSON for a given run ID (used during dispatch).
    /// </summary>
    /// <param name="runId">The run ID to look up.</param>
    /// <returns>The serialized feedback JSON, or null if not cached.</returns>
    string? GetFeedbackDataForRun(RunId runId);

    /// <summary>
    /// Removes cached feedback data after dispatch (cleanup).
    /// </summary>
    /// <param name="runId">The run ID to clear.</param>
    void ClearFeedbackDataForRun(RunId runId);

    /// <summary>
    /// Determines the timestamp of the last successful harness suggestion run.
    /// Returns <see cref="DateTimeOffset.MinValue"/> if no prior run exists.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    Task<DateTimeOffset> GetLastSuccessfulHarnessRunTimestampAsync(CancellationToken ct = default);
}
