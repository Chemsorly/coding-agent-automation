using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;

namespace CodingAgent.Pipeline.Services;

/// <summary>
/// Verdict returned by <see cref="DispatchEligibilityEvaluator"/>.
/// </summary>
public enum EligibilityVerdict
{
    /// <summary>The item is eligible for dispatch.</summary>
    Eligible,

    /// <summary>Blocked by one or more open dependency issues.</summary>
    BlockedByDependency,

    /// <summary>Already being processed — either in live orchestration state or in the in-cycle snapshot.</summary>
    ActiveElsewhere,

    /// <summary>Carries a label that marks it ineligible (e.g. <c>agent:error</c>, <c>agent:needs-refinement</c>).</summary>
    FilteredByLabel,

    /// <summary>Concurrency limit reached (e.g. MaxConcurrentDecompositions exhausted).</summary>
    ConcurrencyExhausted,

    /// <summary>Carries a label that makes it unsuitable for the current work type (e.g. UI backlog "not ready" labels).</summary>
    WrongLabel,

    /// <summary>The agent selector for this WorkItem has been stopped for the current cycle due to a 409 response.</summary>
    SelectorBlocked,
}

/// <summary>
/// The result of a single dispatch-eligibility evaluation.
/// </summary>
/// <param name="Verdict">Why the item is (or is not) eligible.</param>
/// <param name="Reason">Optional human-readable detail for logging (e.g. blocking issue numbers).</param>
public readonly record struct DispatchEligibilityResult(EligibilityVerdict Verdict, string? Reason = null)
{
    /// <summary>True when the item is eligible for dispatch.</summary>
    public bool IsEligible => Verdict == EligibilityVerdict.Eligible;

    // ── Static factory helpers ──────────────────────────────────────────────

    /// <summary>Creates an <see cref="EligibilityVerdict.Eligible"/> result.</summary>
    public static DispatchEligibilityResult Eligible() => new(EligibilityVerdict.Eligible);

    /// <summary>Creates a <see cref="EligibilityVerdict.BlockedByDependency"/> result.</summary>
    /// <param name="blockedBy">Issue numbers that are still open.</param>
    public static DispatchEligibilityResult BlockedByDependency(IReadOnlyList<int> blockedBy)
    {
        var reason = blockedBy.Count > 0
            ? $"Blocked by open issue(s): {string.Join(", ", blockedBy.Select(n => $"#{n}"))}"
            : "Blocked by open dependency";
        return new(EligibilityVerdict.BlockedByDependency, reason);
    }

    /// <summary>Creates an <see cref="EligibilityVerdict.ActiveElsewhere"/> result.</summary>
    public static DispatchEligibilityResult ActiveElsewhere() => new(EligibilityVerdict.ActiveElsewhere);

    /// <summary>Creates a <see cref="EligibilityVerdict.FilteredByLabel"/> result.</summary>
    public static DispatchEligibilityResult FilteredByLabel() => new(EligibilityVerdict.FilteredByLabel);

    /// <summary>Creates a <see cref="EligibilityVerdict.ConcurrencyExhausted"/> result.</summary>
    public static DispatchEligibilityResult ConcurrencyExhausted() => new(EligibilityVerdict.ConcurrencyExhausted);

    /// <summary>Creates a <see cref="EligibilityVerdict.WrongLabel"/> result.</summary>
    public static DispatchEligibilityResult WrongLabel() => new(EligibilityVerdict.WrongLabel);

    /// <summary>Creates a <see cref="EligibilityVerdict.SelectorBlocked"/> result.</summary>
    public static DispatchEligibilityResult SelectorBlocked() => new(EligibilityVerdict.SelectorBlocked);
}

/// <summary>
/// Centralises the dispatch-eligibility decision across all five dispatch sites:
/// <list type="bullet">
///   <item><see cref="EvaluateLabelFilter"/> — label-based ineligibility (site 1 and site 5)</item>
///   <item><see cref="EvaluateActiveElsewhere"/> — deduplication gate (site 1 and site 4)</item>
///   <item><see cref="EvaluateDependencyAsync"/> — open-dependency check (site 1 and site 5)</item>
///   <item><see cref="EvaluateConcurrencyLimit"/> — concurrency-cap gate (site 2)</item>
///   <item><see cref="EvaluateSelectorBlocked"/> — per-selector stop gate (site 3)</item>
/// </list>
/// Stateless — construct with <c>new DispatchEligibilityEvaluator()</c> wherever needed.
/// </summary>
public sealed class DispatchEligibilityEvaluator
{
    /// <summary>
    /// Returns <see cref="DispatchEligibilityResult.FilteredByLabel"/> when any label in
    /// <paramref name="labels"/> is contained in <paramref name="filterSet"/>, otherwise
    /// <see cref="DispatchEligibilityResult.Eligible"/>.
    /// </summary>
    /// <param name="labels">The issue's current label collection.</param>
    /// <param name="filterSet">
    /// Caller-supplied set of ineligible label values. Each call site provides its own set:
    /// <c>DispatchScheduler.Issues.cs</c> passes <c>{AgentLabels.Error, AgentLabels.NeedsRefinement}</c>;
    /// <c>BlockedIssuesService</c> passes its wider <c>NotReadyLabels</c> set.
    /// </param>
    public DispatchEligibilityResult EvaluateLabelFilter(
        IReadOnlyCollection<string> labels,
        IReadOnlySet<string> filterSet)
    {
        // NOTE (issue #3345): A null filterSet silently returns Eligible (no filter applied), which could
        // swallow a call-site programming error without any diagnostic. Consider replacing this with
        // ArgumentNullException.ThrowIfNull(filterSet) and documenting the intended contract explicitly.
        if (labels is null || filterSet is null || labels.Count == 0)
            return DispatchEligibilityResult.Eligible();

        foreach (var label in labels)
        {
            if (filterSet.Contains(label))
                return DispatchEligibilityResult.FilteredByLabel();
        }

        return DispatchEligibilityResult.Eligible();
    }

    /// <summary>
    /// Returns <see cref="DispatchEligibilityResult.ActiveElsewhere"/> when the issue is already
    /// being processed (in live orchestration state or in the in-cycle snapshot), otherwise
    /// <see cref="DispatchEligibilityResult.Eligible"/>.
    /// </summary>
    /// <param name="isBeingProcessed">
    /// Result of <c>IsIssueBeingProcessed</c> (live orchestration state — guards against races
    /// between agent instances). Must be computed by the caller; this evaluator does not own
    /// the orchestration reference.
    /// </param>
    /// <param name="isInActiveSet">
    /// Whether the issue identifier is present in the in-memory cycle snapshot.
    /// </param>
    /// <remarks>
    /// The two boolean inputs correspond to the two logically distinct checks in
    /// <c>IsIssueAlreadyActive</c> in <c>DispatchScheduler.Issues.cs</c>. Both must
    /// be preserved — do not collapse them into a single flag.
    /// </remarks>
    public DispatchEligibilityResult EvaluateActiveElsewhere(bool isBeingProcessed, bool isInActiveSet)
    {
        if (isBeingProcessed || isInActiveSet)
            return DispatchEligibilityResult.ActiveElsewhere();
        return DispatchEligibilityResult.Eligible();
    }

    /// <summary>
    /// Checks whether all dependency references in <paramref name="issueBody"/> are satisfied
    /// (closed). Returns <see cref="DispatchEligibilityResult.BlockedByDependency"/> when any
    /// open dependency exists, otherwise <see cref="DispatchEligibilityResult.Eligible"/>.
    /// </summary>
    /// <param name="identifier">The issue's own identifier (for self-reference filtering).</param>
    /// <param name="issueBody">The issue body text containing dependency references.</param>
    /// <param name="provider">Provider to check referenced issue states.</param>
    /// <param name="stateCache">Shared cache for issue state lookups within a poll cycle.</param>
    /// <param name="checker">The dependency checker implementation.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task<DispatchEligibilityResult> EvaluateDependencyAsync(
        IssueIdentifier identifier,
        string? issueBody,
        IIssueProvider provider,
        Dictionary<int, bool> stateCache,
        IDependencyChecker checker,
        CancellationToken ct)
    {
        // NOTE (issue #3345): provider, checker, and stateCache have no null guards. A null argument would
        // cause a NullReferenceException inside checker.CheckAsync rather than at the guard boundary.
        // Add ArgumentNullException.ThrowIfNull(provider), ThrowIfNull(checker), ThrowIfNull(stateCache).
        var depResult = await checker.CheckAsync(identifier, issueBody, provider, stateCache, ct);
        if (!depResult.IsReady)
            return DispatchEligibilityResult.BlockedByDependency(depResult.BlockedBy);
        return DispatchEligibilityResult.Eligible();
    }

    /// <summary>
    /// Returns <see cref="DispatchEligibilityResult.ConcurrencyExhausted"/> when
    /// <paramref name="activeCount"/> is greater than or equal to <paramref name="maxAllowed"/>,
    /// otherwise <see cref="DispatchEligibilityResult.Eligible"/>.
    /// Used by <c>DispatchDecompositionRoundAsync</c> to guard the per-template concurrency cap.
    /// </summary>
    /// <param name="activeCount">
    /// Total currently-active and newly-dispatched count
    /// (e.g. <c>activeDecompositionCount + additionalDecompDispatches</c>).
    /// </param>
    /// <param name="maxAllowed">Maximum allowed concurrent instances.</param>
    public DispatchEligibilityResult EvaluateConcurrencyLimit(int activeCount, int maxAllowed)
    {
        if (activeCount >= maxAllowed)
            return DispatchEligibilityResult.ConcurrencyExhausted();
        return DispatchEligibilityResult.Eligible();
    }

    /// <summary>
    /// Returns <see cref="DispatchEligibilityResult.SelectorBlocked"/> when the
    /// <paramref name="agentSelector"/> has been added to the stopped-selectors set for the
    /// current cycle, otherwise <see cref="DispatchEligibilityResult.Eligible"/>.
    /// Used by <c>WorkItemDispatchLoop.PollAndDispatchAsync</c> for the per-cycle selector gate.
    /// </summary>
    /// <param name="agentSelector">The WorkItem's agent selector string.</param>
    /// <param name="stoppedSelectors">The per-cycle set of stopped selectors.</param>
    public DispatchEligibilityResult EvaluateSelectorBlocked(
        string agentSelector,
        IReadOnlySet<string> stoppedSelectors)
    {
        // NOTE (issue #3345): agentSelector has no null guard. stoppedSelectors.Contains(null) returns false
        // on a HashSet<string> with OrdinalIgnoreCase comparer, so a null selector silently passes the gate.
        // Add ArgumentNullException.ThrowIfNull(agentSelector) to surface the call-site bug.
        if (stoppedSelectors.Contains(agentSelector))
            return DispatchEligibilityResult.SelectorBlocked();
        return DispatchEligibilityResult.Eligible();
    }
}
