using CodingAgent.Pipeline.Models;

namespace CodingAgent.Api.Client;

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
    /// Executes all five DatabaseMaintenanceService sweep operations and returns counts.
    /// The API is stateless — it always executes when called. The Scheduler's
    /// RetentionSweepSchedulerService gates calls on its own leader election.
    /// </summary>
    Task<RetentionSweepResultDto> TriggerRetentionSweepAsync(CancellationToken ct = default);

    /// <summary>GET /api/work-items/counts-by-status on the API — work item counts grouped by status,
    /// plus the oldest Pending item's creation timestamp for the pending-age gauge.</summary>
    Task<WorkItemCountsResponseDto> GetWorkItemCountsAsync(CancellationToken ct = default);

    /// <summary>
    /// GET /api/agents on the API — returns the total and busy agent counts.
    /// Used by the Scheduler leader to feed the <c>agent.jobs.active</c> and
    /// <c>agent.connections.total</c> observable gauges.
    /// </summary>
    Task<AgentCountsResponseDto> GetAgentCountsAsync(CancellationToken ct = default);

    /// <summary>
    /// GET /api/agents/credential-pool on the API — returns the Kiro credential (PVC) pool snapshot.
    /// Used by the Scheduler leader to feed the <c>workdistribution.credential_pool_available</c>
    /// and <c>workdistribution.credential_pool_claimed</c> observable gauges.
    /// </summary>
    Task<CredentialPoolStatus> GetAgentCredentialPoolAsync(CancellationToken ct = default);
}
