using CodingAgentWebUI.Pipeline.Models;

namespace CodingAgentWebUI.Api.Client;

/// <summary>
/// Loop state snapshot returned by GET /loop/status on the Scheduler.
/// Mirrors the read-only surface of IPipelineLoopService so the WebUI polling adapter
/// can expose the same properties without changing any Blazor component.
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
        new Dictionary<string, ConfigStatusSnapshot>()) { }
}

/// <summary>Response from POST /loop/start.</summary>
public record LoopStartResultDto(bool Started, string? Error);

/// <summary>
/// Result of a full retention sweep triggered via POST /api/scheduler/maintenance/retention-sweep.
/// Each field corresponds to one of the five sweep operations in DatabaseMaintenanceService.
/// </summary>
public record RetentionSweepResultDto(
    int StaleWorkItemsDeleted,
    int StalePipelineRunsDeleted,
    int StaleConsolidationRunsDeleted,
    int RetentionPipelineRunsDeleted,
    int RetentionWorkItemsDeleted);

/// <summary>Work item count grouped by status, returned by GET /api/work-items/counts-by-status.</summary>
public record WorkItemCountDto(string Status, string AgentSelector, long Count);
