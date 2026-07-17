using CodingAgentWebUI.Pipeline.Models;

namespace CodingAgentWebUI.Orchestration.Dispatch;

/// <summary>
/// Holds the pre-fetched issue context needed to build a <see cref="JobAssignmentMessage"/>
/// or a <see cref="DispatchPreparationResult"/>. Produced by <see cref="IssueContextBuilder"/>.
/// </summary>
internal sealed record IssueContext(
    IssueDetail IssueDetail,
    ParsedIssue ParsedIssue,
    IReadOnlyList<IssueComment> IssueComments,
    string? ExistingAnalysis,
    bool ForceRefreshAnalysis,
    string? StalenessSignal,
    int RefreshCount);
