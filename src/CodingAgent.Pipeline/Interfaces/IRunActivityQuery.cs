using CodingAgent.Pipeline.Models;
using CodingAgent.Pipeline.Services;

namespace CodingAgent.Pipeline.Interfaces;

/// <summary>
/// Read-only query interface for active-run activity, used by the Scheduler
/// and housekeeping services that do not need write access to the run registry.
/// </summary>
/// <remarks>
/// <para>
/// <b><see cref="IsIssueBeingProcessed"/> throws <see cref="ArgumentException"/></b> for a null
/// or empty <c>issueIdentifier.Value</c> in all implementations. The distributed implementation
/// additionally returns <c>false</c> (fail-open) when the remote lookup itself throws.
/// </para>
/// </remarks>
public interface IRunActivityQuery
{
    /// <summary>Checks whether the given issue identifier is being processed by any active run.</summary>
    /// <exception cref="ArgumentException">
    /// Thrown when <paramref name="issueIdentifier"/>.Value is null or empty.
    /// </exception>
    bool IsIssueBeingProcessed(IssueIdentifier issueIdentifier, ProviderConfigId issueProviderConfigId);

    /// <summary>
    /// Records that a run for this issue just completed. Used by orphan recovery to guard
    /// against race conditions between run removal and label swap.
    /// </summary>
    void MarkRecentlyCompleted(IssueIdentifier issueIdentifier, ProviderConfigId issueProviderConfigId);

    /// <summary>
    /// Returns <c>true</c> if this issue had a run complete within the last 120 seconds.
    /// </summary>
    bool WasRecentlyCompleted(IssueIdentifier issueIdentifier, ProviderConfigId issueProviderConfigId);

    /// <summary>
    /// Returns the branch names of all currently active pipeline runs.
    /// Uses case-insensitive comparison (<see cref="StringComparer.OrdinalIgnoreCase"/>).
    /// Null branch names are excluded from the result.
    /// </summary>
    /// <remarks>
    /// Used by <see cref="HousekeepingService"/> Step 4 / Step 6b to guard against calling
    /// <c>UpdatePullRequestBranchAsync</c> on a branch that has an active run.
    /// </remarks>
    Task<HashSet<string>> GetActiveRunBranchesAsync(CancellationToken ct = default);
}
