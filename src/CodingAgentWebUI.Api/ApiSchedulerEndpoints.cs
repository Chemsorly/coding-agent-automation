using CodingAgentWebUI.Api.Client;
using CodingAgentWebUI.Orchestration.Dispatch;

namespace CodingAgentWebUI.Api;

/// <summary>
/// Minimal API endpoints called by the Scheduler microservice.
/// These are API-side endpoints — distinct from the Scheduler's own loop-control endpoints
/// which are served from port 8080.
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
    /// The Scheduler's RetentionSweepSchedulerService gates calls on its own leader election,
    /// so only one Scheduler replica triggers the sweep per interval. The API is stateless —
    /// no secondary leader gate is needed here.
    /// </summary>
    private static async Task<IResult> RunRetentionSweep(
        DatabaseMaintenanceService maintenanceService,
        CancellationToken ct)
    {
        var result = await maintenanceService.RunRetentionSweepAsync(ct);

        return Results.Ok(new RetentionSweepResultDto(
            result.StaleWorkItemsDeleted,
            result.StalePipelineRunsDeleted,
            result.StaleConsolidationRunsDeleted,
            result.RetentionPipelineRunsDeleted,
            result.RetentionWorkItemsDeleted,
            result.OrphanedPipelineRunsBackfilled));
    }
}
