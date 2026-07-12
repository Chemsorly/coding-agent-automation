namespace CodingAgentWebUI.Pipeline.Models;

/// <summary>
/// Findings from a single review agent, for inclusion in PR descriptions.
/// </summary>
public sealed record AgentFindings(string AgentName, string Findings);

/// <summary>
/// Bundles code review metadata for inclusion in PR descriptions.
/// Pass <c>null</c> when code review is disabled to omit the section entirely.
/// </summary>
public sealed record CodeReviewSummary(
    IReadOnlyList<string> AgentsRun,
    int CriticalCount,
    int WarningCount,
    int SuggestionCount,
    IReadOnlyList<AgentFindings> AgentFindings)
{
    /// <summary>
    /// AI-generated summary of what the PR changed (2-3 sentences).
    /// Null when summary generation failed or was not run.
    /// </summary>
    public string? ChangeSummary { get; init; }

    /// <summary>
    /// AI-generated synthesis of review findings (1-2 sentences).
    /// Null when summary generation failed or was not run.
    /// </summary>
    public string? VerdictSummary { get; init; }
}
