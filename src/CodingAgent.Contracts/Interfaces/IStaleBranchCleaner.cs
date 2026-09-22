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
    /// Runs stale-branch cleanup if <paramref name="enabled"/> is true and the cleanup interval
    /// has elapsed since the last pass for this repository.
    /// </summary>
    /// <param name="repoProvider">Repository provider used to list and delete branches.</param>
    /// <param name="issueProvider">Issue provider used to check issue labels.</param>
    /// <param name="agentDonePrs">
    /// The current set of open agent PRs (used as a secondary guard for branch protection;
    /// the primary guard is a full open-PR scan via <c>ListOpenPullRequestsAsync</c>).
    /// </param>
    /// <param name="repoProviderId">Repository provider ID, used as the cadence key.</param>
    /// <param name="repoTag">OTel tag for all telemetry emitted during this call.</param>
    /// <param name="enabled">When false, the method returns immediately without doing anything.</param>
    /// <param name="cleanupIntervalMinutes">Minimum minutes between cleanup passes for the same repo.</param>
    /// <param name="ct">Cancellation token.</param>
    Task RunIfDueAsync(
        IRepositoryProvider repoProvider,
        IIssueProvider issueProvider,
        IReadOnlyList<PullRequestSummary> agentDonePrs,
        string repoProviderId,
        KeyValuePair<string, object?> repoTag,
        bool enabled,
        int cleanupIntervalMinutes,
        CancellationToken ct);
}
