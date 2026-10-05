using CodingAgent.Pipeline.Interfaces;

namespace CodingAgent.Pipeline.Models;

/// <summary>
/// Inputs for <see cref="IStaleBranchCleaner.RunIfDueAsync"/>.
/// </summary>
public sealed record StaleBranchCleanupRequest
{
    /// <summary>Repository provider used to list and delete branches.</summary>
    public required IRepositoryProvider RepoProvider { get; init; }

    /// <summary>Issue provider used to check issue labels.</summary>
    public required IIssueProvider IssueProvider { get; init; }

    /// <summary>
    /// The current set of open agent PRs, used as the sole branch-protection guard.
    /// When <see cref="WasInputTruncated"/> is true this list is incomplete and cleanup
    /// is skipped entirely with a Warning log.
    /// </summary>
    public required IReadOnlyList<PullRequestSummary> AgentDonePrs { get; init; }

    /// <summary>
    /// True when <see cref="AgentDonePrs"/> was capped by <c>ClosedLoopMaxPagesToFetch</c>
    /// and may be missing PRs beyond the pagination limit. When true, the cleanup cycle is
    /// skipped with a Warning rather than risking deletion of a branch that has an open PR
    /// not present in the truncated list.
    /// </summary>
    public required bool WasInputTruncated { get; init; }

    /// <summary>Repository provider ID, used as the cadence key.</summary>
    public required string RepoProviderId { get; init; }

    /// <summary>OTel tag for all telemetry emitted during this call.</summary>
    public required KeyValuePair<string, object?> RepoTag { get; init; }

    /// <summary>When false, cleanup returns immediately without doing anything.</summary>
    public required bool Enabled { get; init; }

    /// <summary>Minimum minutes between cleanup passes for the same repo.</summary>
    public required int CleanupIntervalMinutes { get; init; }
}
