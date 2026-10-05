using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;

namespace CodingAgent.Pipeline.Interfaces;

/// <summary>
/// Encapsulates stale-branch cleanup logic: lists agent branches, skips those with an
/// open PR or an active issue label, and deletes the rest. Runs on its own interval cadence
/// so that the per-tick housekeeping loop does not call the repository API on every poll cycle.
/// </summary>
public interface IStaleBranchCleaner
{
    /// <summary>
    /// Runs stale-branch cleanup if <see cref="StaleBranchCleanupRequest.Enabled"/> is true and the
    /// cleanup interval has elapsed since the last pass for this repository.
    /// </summary>
    /// <param name="request">
    /// The repository and issue providers, the open agent PRs (and whether that list was truncated),
    /// the cadence key and telemetry tag, and the enabled flag and cleanup interval.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    Task RunIfDueAsync(StaleBranchCleanupRequest request, CancellationToken ct);
}
