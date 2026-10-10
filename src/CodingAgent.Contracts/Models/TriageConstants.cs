namespace CodingAgent.Pipeline.Models;

/// <summary>
/// Well-known values of the Triage run type. A triage started from the web UI (an operator triage) is
/// not linked to an issue: its WorkItem uses the <see cref="ProviderConfigId"/> sentinel as its tracker
/// and <c>"triage:{triageId}"</c> as its identifier, like consolidation runs use their own sentinel.
/// </summary>
public static class TriageConstants
{
    /// <summary>
    /// Sentinel <see cref="JobDistributionRequest.IssueProviderConfigId"/> of an operator triage. No
    /// provider config has this id, so label and comment operations for such a run find no tracker and
    /// skip.
    /// </summary>
    public const string ProviderConfigId = "triage";

    /// <summary>Prefix of an operator triage's <see cref="JobDistributionRequest.IssueIdentifier"/>.</summary>
    public const string IssueIdentifierPrefix = "triage:";

    /// <summary>The most drafts one attempt may propose; further drafts are dropped.</summary>
    public const int MaxDrafts = 5;

    /// <summary>The most other triages of the project the agent is shown for duplicate detection.</summary>
    public const int MaxHistoryContext = 30;

    /// <summary>The most issue comments a tracker triage reads: the newest ones.</summary>
    public const int MaxCommentsForContext = 50;

    /// <summary>Marks the root cause analysis comment on a tracker issue; a re-run updates it in place.</summary>
    public const string CommentMarker = "<!-- agent:triage-rca -->";

    /// <summary>Marks the comment that lists the issues created from a tracker triage.</summary>
    public const string SummaryMarker = "<!-- agent:triage-summary -->";

    /// <summary>The root cause analysis comment stays below this length (trackers cap comments at 65,536).</summary>
    public const int MaxCommentChars = 60_000;

    /// <summary>A result file above this size fails the run (the agent hub accepts messages up to 128 KB).</summary>
    public const int MaxResultBytes = 100_000;

    /// <summary>A draft body is cut to this length.</summary>
    public const int MaxDraftBodyChars = 12_000;

    /// <summary>Returns the identifier of an operator triage's WorkItem.</summary>
    public static string IssueIdentifierFor(Guid triageId) => IssueIdentifierPrefix + triageId.ToString("D");

    /// <summary>
    /// Reads the triage id back from an operator triage's WorkItem identifier, or returns null when the
    /// identifier is not one.
    /// </summary>
    public static Guid? TryParseTriageId(string? issueIdentifier) =>
        issueIdentifier is not null
        && issueIdentifier.StartsWith(IssueIdentifierPrefix, StringComparison.Ordinal)
        && Guid.TryParse(issueIdentifier.AsSpan(IssueIdentifierPrefix.Length), out var id)
            ? id
            : null;

    /// <summary>True when the tracker id is the operator-triage sentinel.</summary>
    public static bool IsOperatorTriage(string? issueProviderConfigId) =>
        string.Equals(issueProviderConfigId, ProviderConfigId, StringComparison.Ordinal);
}
