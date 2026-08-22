using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;

namespace CodingAgentWebUI.Api;

/// <summary>
/// Minimal API endpoints for consolidation run persistence.
/// Exposes CRUD operations over the ConsolidationRuns table so the orchestrator
/// can delegate all DB access to the API rather than connecting directly.
/// All endpoints require AgentApiKey authentication (operator tier).
/// </summary>
public static class ConsolidationRunEndpoints
{
    public static void MapConsolidationRunEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/consolidation-runs")
            .RequireAuthorization(ApiAuthPolicies.Operator);

        group.MapGet("/", GetAll);
        group.MapGet("/{runId:guid}", GetById);
        group.MapPut("/{runId:guid}", Save);
        group.MapDelete("/{runId:guid}", Delete);
    }

    internal static async Task<IResult> GetAll(
        IConsolidationRunStore store,
        CancellationToken ct)
    {
        var runs = await store.LoadAllRunsAsync(ct);
        return TypedResults.Ok(runs);
    }

    internal static async Task<IResult> GetById(
        Guid runId,
        IConsolidationRunStore store,
        CancellationToken ct)
    {
        var run = await store.GetByIdAsync(new RunId(runId.ToString()), ct);
        return run is null ? TypedResults.NotFound() : TypedResults.Ok(run);
    }

    internal static async Task<IResult> Save(
        Guid runId,
        [Microsoft.AspNetCore.Mvc.FromBody] ConsolidationRun run,
        IConsolidationRunStore store,
        CancellationToken ct)
    {
        // Validate route param matches body RunId to prevent silent mismatch
        if (!Guid.TryParse(run.RunId, out var bodyGuid) || bodyGuid != runId)
            return TypedResults.BadRequest($"Route runId '{runId}' does not match body RunId '{run.RunId}'");
        await store.SaveRunAsync(run, ct);
        return TypedResults.Ok();
    }

    internal static async Task<IResult> Delete(
        Guid runId,
        IConsolidationRunStore store,
        CancellationToken ct)
    {
        await store.DeleteRunAsync(new RunId(runId.ToString()), ct);
        return TypedResults.Ok();
    }
}
