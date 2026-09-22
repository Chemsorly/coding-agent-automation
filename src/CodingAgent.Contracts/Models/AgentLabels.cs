namespace CodingAgent.Pipeline.Models;

/// <summary>
/// Defines the agent status labels applied to GitHub issues during pipeline execution.
/// Only one <c>agent:*</c> label should be present on an issue at a time.
/// </summary>
public static class AgentLabels
{
    public const string Next = "agent:next";
    public const string InProgress = "agent:in-progress";
    public const string Error = "agent:error";
    public const string NeedsRefinement = "agent:needs-refinement";
    public const string WontDo = "agent:wont-do";
    public const string Cancelled = "agent:cancelled";
    public const string Done = "agent:done";

    // Epic decomposition labels
    public const string Epic = "agent:epic";
    public const string EpicReview = "agent:epic-review";
    public const string EpicApproved = "agent:epic-approved";

    // Consolidation label (applied to auto-generated issues)
    public const string Generated = "agent:generated";

    /// <summary>All agent labels with their display colors (without '#' prefix).</summary>
    public static readonly IReadOnlyList<(string Name, string Color)> Definitions = new[]
    {
        (Next, "0e8a16"),
        (InProgress, "1d76db"),
        (Error, "d73a4a"),
        (NeedsRefinement, "fbca04"),
        (WontDo, "cfd3d7"),
        (Cancelled, "c5def5"),
        (Done, "0075ca"),
        (Generated, "bfd4f2"),
        (Epic, "7057ff"),
        (EpicReview, "fbca04"),
        (EpicApproved, "0e8a16")
    };

    /// <summary>All agent label names.</summary>
    public static readonly IReadOnlyList<string> All = Definitions.Select(d => d.Name).ToList().AsReadOnly();

    /// <summary>Labels representing terminal pipeline states — should not be overwritten by recovery services.</summary>
    public static readonly IReadOnlySet<string> TerminalLabels = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        Done, Error, NeedsRefinement, WontDo, Cancelled, EpicReview
    };

    /// <summary>
    /// Labels that make an issue ineligible for dispatch.
    /// When the <c>DispatchLoop</c> sees any of these labels on the upstream issue it cancels the
    /// pending <c>WorkItem</c> rather than dispatching it.
    /// <para>
    /// Includes <c>agent:done</c> — a completed issue must not be re-dispatched automatically.
    /// <c>agent:in-progress</c> is intentionally absent: a second WorkItem for an issue that is
    /// already running is blocked by the partial unique index on <c>WorkItems</c>, not this set.
    /// <c>agent:next</c> is also absent: it is the normal pre-dispatch signal and must not block dispatch.
    /// <c>agent:epic-review</c> is intentionally absent: an epic awaiting human review may still
    /// be picked up by an agent for processing (e.g. posting a summary comment), so it must not
    /// block dispatch.
    /// </para>
    /// </summary>
    // TODO: EpicReview was removed from DispatchIneligibleLabels (previously it blocked dispatch).
    // The change is intentional — see XML comment above — but it breaks the formerly-established subset
    // invariant that all TerminalLabels are also DispatchIneligibleLabels. EpicReview is now in
    // TerminalLabels but NOT in DispatchIneligibleLabels. Any dispatch path that gates solely on
    // DispatchIneligibleLabels (without independent epic-review guards) will now allow re-dispatch of
    // epics awaiting human review. Verify all dispatch paths have independent guards for epic-review state.
    // The test TerminalLabels_IsSubsetOf_DispatchIneligibleLabels was removed to accommodate this change;
    // consider adding a replacement test that explicitly documents which TerminalLabels are intentionally
    // absent from DispatchIneligibleLabels (currently only EpicReview) to prevent silent future drift.
    public static readonly IReadOnlySet<string> DispatchIneligibleLabels = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        Done, Error, NeedsRefinement, WontDo, Cancelled
    };

    /// <summary>
    /// Labels that require explicit human action to set. Agents may not set these via RequestLabelChange.
    /// Every member of this set must also be present in <see cref="All"/>; labels absent from <c>All</c>
    /// would be rejected by the prior guard and never reach the gated-label check.
    /// </summary>
    public static readonly IReadOnlySet<string> DispatchGatedLabels = new HashSet<string>
    {
        EpicApproved
    };

    /// <summary>
    /// Issue labels that indicate the issue is actively queued or in-progress.
    /// Used by housekeeping collaborators to guard both conflict-rework label swaps and stale
    /// branch deletion. An issue bearing any of these labels must not be re-queued or have its
    /// branch deleted — work is either pending dispatch or currently running.
    /// </summary>
    public static readonly IReadOnlySet<string> HousekeepingActiveLabels = new HashSet<string>(StringComparer.Ordinal)
    {
        Next,
        InProgress,
        Epic,
        EpicApproved,
        EpicReview,
    };

    /// <summary>
    /// Issue labels representing an explicit human decision to abandon work.
    /// Conflict rework must not re-queue these issues as <c>agent:next</c>.
    /// Distinct from <see cref="HousekeepingActiveLabels"/> to avoid affecting stale-branch cleanup,
    /// which should still delete branches for these abandoned issues.
    /// <para>
    /// <c>agent:done</c> is intentionally <em>excluded</em>: it means the agent completed a run,
    /// but the resulting PR may still be open and conflicted. An open conflicted PR always needs
    /// rework regardless of the issue's current label.
    /// </para>
    /// <para>
    /// <c>agent:error</c> and <c>agent:needs-refinement</c> are intentionally excluded —
    /// they are human-placed signals that the issue should be re-queued for rework.
    /// </para>
    /// </summary>
    public static readonly IReadOnlySet<string> HousekeepingTerminalReworkBlockers = new HashSet<string>(StringComparer.Ordinal)
    {
        WontDo,
        Cancelled,
    };

    /// <summary>
    /// Precedence ordering for resolving dual-label issues (excludes <see cref="Generated"/>,
    /// which is orthogonal and may legitimately coexist with any status label).
    /// When multiple agent:* labels are found on a single issue, the label with the lowest
    /// index in this list is retained and all others are removed.
    /// Terminal/completed states take priority over active states, reflecting the intended
    /// direction of the swap that left the issue in the dual-label state.
    /// </summary>
    public static readonly IReadOnlyList<string> DualLabelResolutionPrecedence = new[]
    {
        Done, Error, NeedsRefinement, WontDo, Cancelled, EpicReview, EpicApproved,
        InProgress, Epic, Next
    };
}
