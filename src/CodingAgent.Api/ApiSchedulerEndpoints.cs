using CodingAgent.Api.Client;
using CodingAgent.Api.Loop;
using CodingAgent.Orchestration.Dispatch;

namespace CodingAgent.Api;

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
        group.MapPut("/loop-status", ReportLoopStatus);
    }

    // ── POST /api/scheduler/maintenance/retention-sweep ───────────────────────

    /// <summary>
    /// Executes all DatabaseMaintenanceService sweep operations and returns counts.
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
            result.RetentionPipelineRunsDeleted,
            result.RetentionWorkItemsDeleted,
            result.OrphanedPipelineRunsReconciled,
            result.TriageAttemptsBackfilled,
            result.TriagesDeleted));
    }

    // ── PUT /api/scheduler/loop-status ────────────────────────────────────────

    /// <summary>
    /// Stores the loop status the Scheduler leader pushes on every change (spec 051 D11). The UI reads
    /// it through <c>GET /api/ui/loop/status</c>.
    /// </summary>
    internal static async Task<IResult> ReportLoopStatus(LoopStatusDto status, LoopStatusStore store)
    {
        await store.SaveAsync(status);
        return TypedResults.NoContent();
    }
}
