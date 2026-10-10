using System.Text.Json.Serialization;

namespace CodingAgent.Pipeline.Models;

/// <summary>The status of a triage as the UI shows it.</summary>
public enum TriageStatus
{
    [JsonStringEnumMemberName("new")]
    New,

    [JsonStringEnumMemberName("investigating")]
    Investigating,

    [JsonStringEnumMemberName("needs_review")]
    NeedsReview,

    [JsonStringEnumMemberName("needs_input")]
    NeedsInput,

    [JsonStringEnumMemberName("not_a_bug")]
    NotABug,

    [JsonStringEnumMemberName("duplicate")]
    Duplicate,

    [JsonStringEnumMemberName("issues_created")]
    IssuesCreated,

    [JsonStringEnumMemberName("dismissed")]
    Dismissed,

    [JsonStringEnumMemberName("failed")]
    Failed,

    [JsonStringEnumMemberName("cancelled")]
    Cancelled
}

/// <summary>The tabs of the triage list.</summary>
public enum TriageListTab
{
    [JsonStringEnumMemberName("all")]
    All,

    /// <summary>Waiting for a person: drafts to review or questions to answer.</summary>
    [JsonStringEnumMemberName("need_you")]
    NeedYou,

    [JsonStringEnumMemberName("investigating")]
    Investigating,

    /// <summary>Everything that is neither running nor waiting for a person.</summary>
    [JsonStringEnumMemberName("done")]
    Done
}

/// <summary>Combines a triage's stored state with whether an attempt is running.</summary>
public static class TriageStatusResolver
{
    /// <summary>
    /// A running attempt wins, then a person's decision (issues created, dismissed). Otherwise a last
    /// attempt without a result ended without reporting one and shows as failed or cancelled; an earlier
    /// result stays visible on the page. Otherwise the stored state decides.
    /// </summary>
    public static TriageStatus Resolve(TriageRecord record, bool isActive)
    {
        ArgumentNullException.ThrowIfNull(record);
        var last = record.Attempts.Count > 0 ? record.Attempts[^1] : null;
        return Resolve(record.State, isActive, last is not null, last?.Result is not null, last?.Outcome);
    }

    /// <summary>
    /// <see cref="Resolve(TriageRecord, bool)"/> from the few facts it needs, so a list can resolve
    /// statuses without loading whole records.
    /// </summary>
    public static TriageStatus Resolve(
        TriageState state, bool isActive, bool hasAttempts, bool lastAttemptHasResult, TriageAttemptOutcome? lastAttemptOutcome)
    {
        if (isActive)
            return TriageStatus.Investigating;

        if (state == TriageState.IssuesCreated)
            return TriageStatus.IssuesCreated;
        if (state == TriageState.Dismissed)
            return TriageStatus.Dismissed;

        if (!hasAttempts)
            return TriageStatus.New;
        if (!lastAttemptHasResult)
            return lastAttemptOutcome == TriageAttemptOutcome.Cancelled ? TriageStatus.Cancelled : TriageStatus.Failed;

        return state switch
        {
            TriageState.NeedsReview => TriageStatus.NeedsReview,
            TriageState.NeedsInput => TriageStatus.NeedsInput,
            TriageState.NotABug => TriageStatus.NotABug,
            TriageState.Duplicate => TriageStatus.Duplicate,
            _ => TriageStatus.New
        };
    }

    /// <summary>The state a result moves a triage to.</summary>
    public static TriageState StateFor(TriageVerdict verdict) => verdict switch
    {
        TriageVerdict.CauseFound => TriageState.NeedsReview,
        TriageVerdict.Inconclusive => TriageState.NeedsInput,
        TriageVerdict.NotABug => TriageState.NotABug,
        TriageVerdict.Duplicate => TriageState.Duplicate,
        _ => TriageState.NeedsReview
    };

    /// <summary>True when the status belongs to the tab.</summary>
    public static bool IsInTab(TriageStatus status, TriageListTab tab) => tab switch
    {
        TriageListTab.NeedYou => status is TriageStatus.NeedsReview or TriageStatus.NeedsInput,
        TriageListTab.Investigating => status is TriageStatus.Investigating,
        TriageListTab.Done => status is not (TriageStatus.NeedsReview or TriageStatus.NeedsInput or TriageStatus.Investigating),
        _ => true
    };
}
