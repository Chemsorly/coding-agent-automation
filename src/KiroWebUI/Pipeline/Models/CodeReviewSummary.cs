namespace KiroWebUI.Pipeline.Models;

/// <summary>
/// Bundles code review metadata for inclusion in PR descriptions.
/// Pass <c>null</c> when code review is disabled to omit the section entirely.
/// </summary>
public sealed record CodeReviewSummary(
    IReadOnlyList<string> AgentsRun,
    string Tier,
    int CriticalCount,
    int WarningCount,
    int SuggestionCount,
    string? RawFindings,
    int FilesChanged,
    int LinesChanged);
