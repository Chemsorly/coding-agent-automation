namespace CodingAgentWebUI.Pipeline.Models;

/// <summary>
/// Represents the mergeability state of a pull request as reported by the provider.
/// Used by <c>IRepositoryProvider.IsPullRequestBehindBaseAsync</c> to allow
/// <see cref="Services.HousekeepingService"/> to distinguish conflicts from other non-behind states.
/// </summary>
public enum PrMergeabilityStatus
{
    /// <summary>
    /// Branch is behind the base — trigger <c>UpdatePullRequestBranchAsync</c>.
    /// </summary>
    Behind,

    /// <summary>
    /// Branch is up-to-date and conflict-free — no action needed, free the in-flight slot.
    /// </summary>
    UpToDate,

    /// <summary>
    /// Branch has a merge conflict — trigger rework label swap to <c>agent:next</c>.
    /// Frees the in-flight slot; the rework run handles conflict resolution.
    /// </summary>
    Conflicted,

    /// <summary>
    /// Required status checks are still running or mergeability is still being computed
    /// (e.g., GitHub <c>"blocked"</c> or <c>"unknown"</c> during active CI).
    /// Keep the in-flight slot occupied; re-evaluate on the next tick.
    /// CRITICAL: Do NOT map GitHub <c>"blocked"</c> to any other value — it is returned
    /// for the full CI run duration (5–30+ min) when required checks are configured.
    /// </summary>
    Blocked,

    /// <summary>
    /// Unrecognised value — conservative wait, keep slot occupied.
    /// </summary>
    Unknown,
}
