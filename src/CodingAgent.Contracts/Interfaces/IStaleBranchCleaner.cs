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
    /// The current set of open agent PRs, used as the sole branch-protection guard.
    /// When <paramref name="wasInputTruncated"/> is true this list is incomplete and cleanup
    /// is skipped entirely with a Warning log.
    /// </param>
    /// <param name="wasInputTruncated">
    /// True when <paramref name="agentDonePrs"/> was capped by <c>ClosedLoopMaxPagesToFetch</c>
    /// and may be missing PRs beyond the pagination limit. When true, the cleanup cycle is
    /// skipped with a Warning rather than risking deletion of a branch that has an open PR
    /// not present in the truncated list.
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
        bool wasInputTruncated,
        string repoProviderId,
        KeyValuePair<string, object?> repoTag,
        bool enabled,
        int cleanupIntervalMinutes,
        CancellationToken ct);
}
