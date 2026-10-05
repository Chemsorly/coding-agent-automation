using CodingAgent.Pipeline.Interfaces;

namespace CodingAgent.Pipeline.Models;

/// <summary>
/// Inputs for <see cref="IIssueReworkService.TriggerConflictReworkAsync"/>: the ordered PR candidates,
/// their mergeability, the branches occupied by active runs, and the providers used to swap labels.
/// </summary>
public sealed record ConflictReworkRequest
{
    /// <summary>Ordered PR candidates (conflict check iterates this list).</summary>
    public required IReadOnlyList<PullRequestSummary> Sorted { get; init; }

    /// <summary>Mergeability status keyed by PR number.</summary>
    public required IReadOnlyDictionary<int, PrMergeabilityStatus> MergeabilityMap { get; init; }

    /// <summary>Set of branch names currently occupied by active pipeline runs.</summary>
    public required IReadOnlySet<string> ActiveRunBranches { get; init; }

    /// <summary>
    /// When true, active-run data was unavailable — all rework is skipped conservatively.
    /// </summary>
    public required bool ActiveRunBranchesUnavailable { get; init; }

    /// <summary>Repository provider used to extract linked issues.</summary>
    public required IRepositoryProvider RepoProvider { get; init; }

    /// <summary>Issue provider used to get and mutate issue labels.</summary>
    public required IIssueProvider IssueProvider { get; init; }

    /// <summary>Issue provider ID for logging context.</summary>
    public required string IssueProviderId { get; init; }

    /// <summary>OTel tag for telemetry emitted during this call.</summary>
    public required KeyValuePair<string, object?> RepoTag { get; init; }
}
