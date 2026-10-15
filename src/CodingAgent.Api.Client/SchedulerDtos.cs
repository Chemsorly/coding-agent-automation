using CodingAgent.Pipeline.Models;

namespace CodingAgent.Api.Client;

/// <summary>
/// The loop state the Scheduler leader pushes to PUT /api/scheduler/loop-status (spec 051 D11).
/// Mirrors the read-only surface of IPipelineLoopService; the API serves it to the UI as
/// <c>UiLoopStatus</c>, filtered for project-scoped users.
/// </summary>
public record LoopStatusDto(
    bool IsLoopActive,
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
    IReadOnlyDictionary<string, ConfigStatusSnapshot> TemplateStatuses)
{
    /// <summary>Parameterless constructor for JSON deserialization.</summary>
    public LoopStatusDto() : this(
        false, "", null, 0, 0, 0, false, null, 0, 0,
        Array.Empty<string>(),
        new Dictionary<string, ConfigStatusSnapshot>())
    { }
}

/// <summary>
/// Result of a full retention sweep triggered via POST /api/scheduler/maintenance/retention-sweep.
/// Each field corresponds to one of the sweep operations in DatabaseMaintenanceService.
/// </summary>
public record RetentionSweepResultDto(
    int StaleWorkItemsDeleted,
    int StalePipelineRunsDeleted,
    int RetentionPipelineRunsDeleted,
    int RetentionWorkItemsDeleted,
    int OrphanedPipelineRunsReconciled = 0,
    int TriageAttemptsBackfilled = 0,
    int TriagesDeleted = 0);

/// <summary>Work item count grouped by status, returned by GET /api/work-items/counts-by-status.</summary>
public record WorkItemCountDto(string Status, string AgentSelector, long Count);

/// <summary>
/// Response wrapper for <c>GET /api/work-items/counts-by-status</c>.
/// Contains work item counts grouped by (Status, AgentSelector) plus the oldest Pending item's
/// creation timestamp, used by the Scheduler to feed the <c>workdistribution.pending.oldest_age_seconds</c> gauge.
/// </summary>
/// <param name="Counts">Work item counts by status and agent selector.</param>
/// <param name="OldestPendingCreatedAt">
/// The <c>CreatedAt</c> of the oldest Pending work item, or <see langword="null"/> when there are none.
/// </param>
public record WorkItemCountsResponseDto(WorkItemCountDto[] Counts, DateTimeOffset? OldestPendingCreatedAt);
