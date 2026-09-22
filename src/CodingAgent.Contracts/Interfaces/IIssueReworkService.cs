using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;

namespace CodingAgent.Pipeline.Interfaces;

/// <summary>
/// Encapsulates conflict-rework label mutation logic: for each conflicted PR whose branch has
/// no active run, extracts linked issues and swaps eligible issue labels to <c>agent:next</c>
/// to trigger a rework dispatch run.
/// </summary>
public interface IIssueReworkService
{
    /// <summary>
    /// For each conflicted PR in <paramref name="sorted"/>, swaps the linked issue's label
    /// to <c>agent:next</c> to trigger a rework dispatch run — unless the PR's branch has an
    /// active run or active-run data was unavailable.
    /// </summary>
    /// <param name="sorted">Ordered PR candidates (conflict check iterates this list).</param>
    /// <param name="mergeabilityMap">Mergeability status keyed by PR number.</param>
    /// <param name="activeRunBranches">Set of branch names currently occupied by active pipeline runs.</param>
    /// <param name="activeRunBranchesUnavailable">
    /// When true, active-run data was unavailable — all rework is skipped conservatively.
    /// </param>
    /// <param name="repoProvider">Repository provider used to extract linked issues.</param>
    /// <param name="issueProvider">Issue provider used to get and mutate issue labels.</param>
    /// <param name="issueProviderId">Issue provider ID for logging context.</param>
    /// <param name="repoTag">OTel tag for telemetry emitted during this call.</param>
    /// <param name="ct">Cancellation token.</param>
    Task TriggerConflictReworkAsync(
        IReadOnlyList<PullRequestSummary> sorted,
        IReadOnlyDictionary<int, PrMergeabilityStatus> mergeabilityMap,
        IReadOnlySet<string> activeRunBranches,
        bool activeRunBranchesUnavailable,
        IRepositoryProvider repoProvider,
        IIssueProvider issueProvider,
        string issueProviderId,
        KeyValuePair<string, object?> repoTag,
        CancellationToken ct);
}
