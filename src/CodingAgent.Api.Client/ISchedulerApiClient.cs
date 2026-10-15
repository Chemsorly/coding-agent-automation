using CodingAgent.Pipeline.Models;

namespace CodingAgent.Api.Client;

/// <summary>
/// HTTP client the Scheduler uses for the API's maintenance and metrics endpoints, and for pushing the
/// loop status (spec 051 D11). Loop control itself is desired state in the pipeline config, so nothing
/// calls the Scheduler.
/// </summary>
public interface ISchedulerApiClient
{
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
    /// PUT /api/scheduler/loop-status on the API — the Scheduler leader's loop status, which the API
    /// serves to the UI (spec 051 D11).
    /// </summary>
    Task ReportLoopStatusAsync(LoopStatusDto status, CancellationToken ct = default);

    /// <summary>
    /// GET /api/agents on the API — all registered agents regardless of status.
    /// Used by <c>WorkItemCountsService</c> to compute the <c>agent.jobs.active</c> and
    /// <c>agent.connections.total</c> gauges from the Scheduler leader.
    /// </summary>
    Task<IReadOnlyList<AgentEntryDto>> GetAgentsAsync(CancellationToken ct = default);

    /// <summary>
    /// GET /api/agents/credential-pool on the API — Kiro credential PVC pool snapshot.
    /// Used by <c>WorkItemCountsService</c> to compute the <c>workdistribution.credential_pool_available</c>
    /// and <c>workdistribution.credential_pool_claimed</c> gauges from the Scheduler leader.
    /// </summary>
    Task<CredentialPoolStatus> GetCredentialPoolAsync(CancellationToken ct = default);
}
