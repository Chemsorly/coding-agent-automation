namespace CodingAgentWebUI.Api.Client;

/// <summary>
/// HTTP client for the Scheduler microservice's loop-control endpoints and maintenance endpoints.
/// The WebUI uses this to start/stop/resume the loop and poll status.
/// The Scheduler uses TriggerRetentionSweepAsync and GetWorkItemCountsAsync to call the API.
/// </summary>
public interface ISchedulerApiClient
{
    /// <summary>GET /loop/status — current loop state snapshot.</summary>
    Task<LoopStatusDto> GetLoopStatusAsync(CancellationToken ct = default);

    /// <summary>POST /loop/start — start the pipeline loop; also persists ClosedLoopAutoStart=true.</summary>
    Task<LoopStartResultDto> StartLoopAsync(CancellationToken ct = default);

    /// <summary>POST /loop/stop — stop the pipeline loop; also persists ClosedLoopAutoStart=false.</summary>
    Task StopLoopAsync(CancellationToken ct = default);

    /// <summary>POST /loop/resume — resume the loop after circuit-breaker trip.</summary>
    Task ResumeLoopAsync(CancellationToken ct = default);

    /// <summary>
    /// POST /api/scheduler/maintenance/retention-sweep on the API.
    /// Throws <see cref="RetentionSweepUnavailableException"/> when API returns 503 (not leader).
    /// </summary>
    Task<RetentionSweepResultDto> TriggerRetentionSweepAsync(CancellationToken ct = default);

    /// <summary>GET /api/work-items/counts-by-status on the API — work item counts grouped by status.</summary>
    Task<WorkItemCountDto[]> GetWorkItemCountsAsync(CancellationToken ct = default);
}
