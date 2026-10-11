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

    // Triage labels: agent:triage starts a triage of the issue; agent:triage-review means an attempt
    // finished and its root cause analysis is posted. The drafts are approved in the app, never by a label.
    public const string Triage = "agent:triage";
    public const string TriageReview = "agent:triage-review";

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
        (EpicApproved, "0e8a16"),
        (Triage, "0b7285"),
        (TriageReview, "fbca04")
    };

    /// <summary>All agent label names. Membership checks are case-insensitive.</summary>
    public static readonly IReadOnlySet<string> All =
        new HashSet<string>(Definitions.Select(d => d.Name), StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Agent label names that are replaced during a status swap.
    /// This is <see cref="All"/> minus <see cref="Generated"/>: <c>agent:generated</c> is a
    /// provenance/kind label that is orthogonal to pipeline status and must survive label swaps
    /// (e.g. swapping a generated issue to <c>agent:in-progress</c> must not strip
    /// <c>agent:generated</c>). Only <see cref="AgentLabelOperations.SwapAsync"/> uses this set;
    /// <see cref="AgentLabelOperations.RemoveAllAsync"/> continues to use <see cref="All"/> because
    /// an explicit full-cleanup intentionally removes every agent label including provenance labels.
    /// Membership checks are case-insensitive.
    /// </summary>
    public static readonly IReadOnlySet<string> SwapTargets =
        new HashSet<string>(
            Definitions.Select(d => d.Name).Where(l => !string.Equals(l, Generated, StringComparison.OrdinalIgnoreCase)),
            StringComparer.OrdinalIgnoreCase);

    /// <summary>Labels representing terminal pipeline states — should not be overwritten by recovery services.</summary>
    public static readonly IReadOnlySet<string> TerminalLabels = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        Done, Error, NeedsRefinement, WontDo, Cancelled, EpicReview, TriageReview
    };

    /// <summary>
    /// Labels that make an issue ineligible for dispatch: every member of <see cref="TerminalLabels"/>.
    /// Manual dispatch from the issue drawer treats them as a force-requeue: it clears them and sets
    /// <c>agent:next</c> before creating the <c>WorkItem</c>.
    /// <para>
    /// Includes <c>agent:done</c> — a completed issue must not be re-dispatched automatically.
    /// Includes <c>agent:epic-review</c> — an epic awaiting approval of its plan continues only once a
    /// person sets <c>agent:epic-approved</c> (decisions.md, "Epic decomposition: two-phase with human
    /// gate"), so the drawer rejects its manual dispatch instead of force-requeuing it.
    /// Includes <c>agent:triage-review</c> for the same reason: a triaged issue waits for a person to
    /// create its drafts in the app or to request a re-run.
    /// <c>agent:in-progress</c> is intentionally absent: a second WorkItem for an issue that is
    /// already running is blocked by the partial unique index on <c>WorkItems</c>, not this set.
    /// <c>agent:next</c> is also absent: it is the normal pre-dispatch signal and must not block dispatch.
    /// </para>
    /// </summary>
    public static readonly IReadOnlySet<string> DispatchIneligibleLabels = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        Done, Error, NeedsRefinement, WontDo, Cancelled, EpicReview, TriageReview
    };

    /// <summary>
    /// Labels that require explicit human action to set. Agents may not set these via RequestLabelChange.
    /// Every member of this set must also be present in <see cref="All"/>; labels absent from <c>All</c>
    /// would be rejected by the prior guard and never reach the gated-label check.
    /// Membership checks are case-insensitive.
    /// </summary>
    public static readonly IReadOnlySet<string> DispatchGatedLabels = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        EpicApproved
    };

    /// <summary>
    /// Issue labels that indicate the issue is actively queued or in-progress.
    /// Used by housekeeping collaborators to guard both conflict-rework label swaps and stale
    /// branch deletion. An issue bearing any of these labels must not be re-queued or have its
    /// branch deleted — work is either pending dispatch or currently running.
    /// Membership checks are case-insensitive.
    /// </summary>
    public static readonly IReadOnlySet<string> HousekeepingActiveLabels = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
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
    /// Membership checks are case-insensitive.
    /// </summary>
    public static readonly IReadOnlySet<string> HousekeepingTerminalReworkBlockers = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        WontDo,
        Cancelled,
    };

    /// <summary>
    /// Agent labels that are allowed on newly-created issues.
    /// All other <c>agent:*</c> labels are dropped when an agent requests issue creation.
    /// Membership checks are case-insensitive.
    /// </summary>
    public static readonly IReadOnlySet<string> AllowedOnCreation = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        Next,
        Generated
    };

    /// <summary>
    /// Filters a caller-supplied label list for use in issue creation.
    /// Keeps non-agent labels (anything not in <see cref="All"/>) and labels in
    /// <see cref="AllowedOnCreation"/> (<c>agent:next</c> and <c>agent:generated</c>).
    /// All other <c>agent:*</c> labels are silently dropped; callers that need to log
    /// dropped labels should inspect the return value against the input.
    /// Comparisons are case-insensitive so mixed-case labels (e.g. "Agent:Epic-Approved")
    /// are correctly classified and dropped.
    /// </summary>
    public static IReadOnlyList<string> FilterForIssueCreation(IEnumerable<string> labels)
    {
        ArgumentNullException.ThrowIfNull(labels);
        // TODO: This guard uses string.IsNullOrEmpty while the parallel guard in CreateSubIssuesStep.cs
        // uses string.IsNullOrWhiteSpace. A label consisting only of whitespace (e.g. "   ") passes
        // IsNullOrEmpty, is not found in All, and is forwarded to the provider. Consider switching to
        // IsNullOrWhiteSpace for consistency with the CreateSubIssuesStep path.
        return labels
            .Where(l => !string.IsNullOrEmpty(l) &&
                        (!All.Contains(l) || AllowedOnCreation.Contains(l)))
            .ToList()
            .AsReadOnly();
    }

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
        Done, Error, NeedsRefinement, WontDo, Cancelled, EpicReview, TriageReview, EpicApproved,
        InProgress, Epic, Triage, Next
    };
}
