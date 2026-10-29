using CodingAgent.Pipeline.Models;

namespace CodingAgent.Pipeline.Brain;

/// <summary>
/// Well-known refusal reason codes for a brain review push (spec 054, req 13.2).
/// </summary>
public static class BrainReviewRefusalReasons
{
    public const string DisallowedPath = "disallowed_path";
    public const string MissingMarker = "missing_marker";
    public const string OverBudget = "over_budget";
    public const string InvalidLine = "invalid_line";
    public const string SharedLineContent = "shared_line_content";
    public const string Shrank = "shrank";
    public const string PendingInvalid = "pending_invalid";
    public const string SummaryMissing = "summary_missing";
    public const string SummaryInvalid = "summary_invalid";
    public const string SkillFileChangedRemotely = BrainReviewPushRefusedException.SkillFileChangedRemotely;

    /// <summary>All valid refusal reasons.</summary>
    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal)
    {
        DisallowedPath, MissingMarker, OverBudget, InvalidLine, SharedLineContent,
        Shrank, PendingInvalid, SummaryMissing, SummaryInvalid, SkillFileChangedRemotely
    };
}
