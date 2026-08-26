using CodingAgentWebUI.Pipeline.Models;

namespace CodingAgentWebUI.Pipeline.Interfaces;

/// <summary>
/// Branch housekeeping and mergeability operations.
/// Consumed exclusively by <c>HousekeepingService</c> (mergeability polling, server-side
/// branch updates, branch cleanup) and the pipeline loop's server-side-update guard.
/// </summary>
public interface IPullRequestHousekeepingProvider : IAsyncDisposable
{
    /// <summary>
    /// Whether this provider supports server-side branch updates via
    /// <see cref="UpdatePullRequestBranchAsync"/> without requiring a local workspace clone.
    /// Default: false (default-deny for providers that have not implemented it).
    /// </summary>
    bool SupportsServerSideBranchUpdate => false;

    /// <summary>
    /// Returns the mergeability status of the PR branch relative to the base branch.
    /// </summary>
    /// <returns>
    /// <see cref="PrMergeabilityStatus.Behind"/> if the branch is behind and a server-side update should be triggered;
    /// <see cref="PrMergeabilityStatus.UpToDate"/> if the branch is clean and no action is needed;
    /// <see cref="PrMergeabilityStatus.Conflicted"/> if there is a merge conflict — the linked issue should be re-queued for rework;
    /// <see cref="PrMergeabilityStatus.Blocked"/> if required checks are still running or mergeability is being computed — keep the in-flight slot;
    /// <see cref="PrMergeabilityStatus.Unknown"/> for any unrecognised value — conservative wait.
    /// CRITICAL: GitHub <c>"blocked"</c> MUST map to <see cref="PrMergeabilityStatus.Blocked"/>, NOT <see cref="PrMergeabilityStatus.UpToDate"/>.
    /// </returns>
    Task<PrMergeabilityStatus> IsPullRequestBehindBaseAsync(int prNumber, CancellationToken ct)
        => Task.FromResult(PrMergeabilityStatus.Unknown);

    /// <summary>
    /// Triggers a server-side branch update (e.g. GitHub: <c>PUT .../update-branch</c>;
    /// GitLab: <c>PUT .../rebase</c>). Does not require a local workspace clone.
    /// Default throws <see cref="NotSupportedException"/>.
    /// </summary>
    Task UpdatePullRequestBranchAsync(int prNumber, CancellationToken ct)
        => throw new NotSupportedException(
            $"{GetType().Name} does not support UpdatePullRequestBranchAsync.");

    /// <summary>
    /// Extracts linked issue references from a pull request. Provider-dependent.
    /// Returns issue identifiers (e.g., "42", "PROJ-123"). Default returns empty.
    /// </summary>
    Task<IReadOnlyList<string>> ExtractLinkedIssuesAsync(int prNumber, CancellationToken ct)
        => Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());

    /// <summary>
    /// Lists all remote branches whose name starts with the agent branch prefix.
    /// Default returns empty.
    /// </summary>
    Task<IReadOnlyList<string>> ListAgentBranchesAsync(CancellationToken ct)
        => Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());

    /// <summary>
    /// Deletes a remote branch by name. No-op if the branch does not exist.
    /// Default throws <see cref="NotSupportedException"/>.
    /// </summary>
    Task DeleteBranchAsync(string branchName, CancellationToken ct)
        => throw new NotSupportedException(
            $"{GetType().Name} does not support DeleteBranchAsync.");
}
