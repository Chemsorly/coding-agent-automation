namespace KiroWebUI.Pipeline.Models;

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
    IReadOnlyList<AgentFindings> AgentFindings);
