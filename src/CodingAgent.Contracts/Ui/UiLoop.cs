using CodingAgent.Pipeline.Models;

namespace CodingAgent.Contracts.Ui;

/// <summary>
/// The pipeline loop as the Scheduler leader last reported it (<c>GET /api/ui/loop/status</c>), plus
/// whether a user asked it to run. While <see cref="RunRequested"/> and <see cref="IsLoopActive"/>
/// differ, a Start or Stop is still being applied; a Start the Scheduler refused shows its reasons in
/// <see cref="ValidationErrors"/>.
/// <para>
/// A project-scoped user sees only the template statuses of their projects, and none of the messages,
/// which can name other projects' templates and issues (spec 051 D3 rule 5).
/// </para>
/// </summary>
/// <param name="RunRequested">Start was the last loop command (the stored <c>closedLoopAutoStart</c>).</param>
/// <param name="ReportedAt">When the Scheduler sent this status; null when it has not reported recently.</param>
public sealed record UiLoopStatus(
    bool IsLoopActive,
    bool RunRequested,
    string StatusMessage,
    string? CurrentIssueIdentifier,
    int ProcessedCount,
    int FailedCount,
    int QueueCount,
    bool IsCircuitBroken,
    string? LastPollError,
    int CurrentCycleTemplateIndex,
    int CurrentCycleTemplateCount,
    IReadOnlyList<string> ValidationErrors,
    IReadOnlyDictionary<string, ConfigStatusSnapshot> TemplateStatuses,
    DateTimeOffset? ReportedAt);
