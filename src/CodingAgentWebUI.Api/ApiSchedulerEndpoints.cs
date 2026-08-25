using CodingAgentWebUI.Api.Client;
using CodingAgentWebUI.Orchestration.Dispatch;
using CodingAgentWebUI.Pipeline.LeaderElection;

namespace CodingAgentWebUI.Api;

/// <summary>
/// Minimal API endpoints called by the Scheduler microservice.
/// These are API-side endpoints — distinct from the Scheduler's own loop-control endpoints
/// which are served from port 8091.
///
/// Authentication: operator key (Bearer token via existing ApiAuthPolicies.Operator policy).
/// </summary>
public static class ApiSchedulerEndpoints
{
    public static void MapApiSchedulerEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/scheduler")
            .RequireAuthorization(ApiAuthPolicies.Operator);

        group.MapPost("/maintenance/retention-sweep", RunRetentionSweep);
    }

    // ── POST /api/scheduler/maintenance/retention-sweep ───────────────────────

    /// <summary>
    /// Executes all five DatabaseMaintenanceService sweep operations and returns counts.
    /// Returns 503 when this API replica is not the leader — the Scheduler retries on the
    /// next tick (typically 60 minutes). Only the leader API replica should execute the sweep
    /// to avoid concurrent bulk deletes across replicas.
    /// </summary>
    private static async Task<IResult> RunRetentionSweep(
        DatabaseMaintenanceService maintenanceService,
        ILeaderElectionService? leaderElection,
        CancellationToken ct)
    {
        // Leader-gate: only the leader API replica executes the sweep.
        // When leaderElection is null (no K8s / test env), allow execution unconditionally —
        // same behaviour as DatabaseMaintenanceService.RunMaintenanceCycleAsync.
        if (leaderElection is not null && !leaderElection.IsLeader)
        {
            return Results.Problem(
                title: "Not leader",
                detail: "This API replica is not the leader. The Scheduler should retry on the next interval.",
                statusCode: StatusCodes.Status503ServiceUnavailable,
                extensions: new Dictionary<string, object?> { ["reason"] = "not_leader" });
        }

        var result = await maintenanceService.RunRetentionSweepAsync(ct);

        return Results.Ok(new RetentionSweepResultDto(
            result.StaleWorkItemsDeleted,
            result.StalePipelineRunsDeleted,
            result.StaleConsolidationRunsDeleted,
            result.RetentionPipelineRunsDeleted,
            result.RetentionWorkItemsDeleted));
    }
}
