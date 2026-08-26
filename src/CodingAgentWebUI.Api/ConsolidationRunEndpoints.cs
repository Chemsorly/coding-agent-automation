using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using Microsoft.AspNetCore.Mvc;

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

        // Called by the Job Controller's ConsolidationDispatchLoop to transition run status
        // (Queued→Running on dispatch success, any→Failed on dispatch failure).
        // Delegates to IConsolidationService so cache invalidation, OnChange events, and
        // workspace management are handled correctly.
        group.MapPost("/{runId:guid}/transition", TransitionStatus);
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

    // ── POST /{runId}/transition ───────────────────────────────────────────────

    /// <summary>
    /// POST /api/consolidation-runs/{runId}/transition
    /// Transitions a ConsolidationRun status via IConsolidationService, which handles
    /// cache invalidation, OnChange events, and workspace management.
    /// Body: { "status": "Running"|"Failed"|..., "summary": "optional message" }
    /// Returns 200, 404 if run not found, 400 if status value is invalid.
    /// </summary>
    internal static async Task<IResult> TransitionStatus(
        Guid runId,
        [FromBody] ConsolidationRunTransitionRequest request,
        IConsolidationService? consolidationService,
        IConsolidationRunStore store,
        CancellationToken ct)
    {
        // Validate the run exists
        var run = await store.GetByIdAsync(new RunId(runId.ToString()), ct);
        if (run is null)
            return TypedResults.NotFound();

        if (consolidationService is not null)
        {
            await consolidationService.UpdateRunAsync(
                new RunId(runId.ToString()),
                request.Status,
                request.Summary,
                ct);
        }
        else
        {
            // Fallback: direct store write when IConsolidationService is unavailable (test/local)
            run.Status = request.Status;
            run.Summary = request.Summary ?? run.Summary;
            if (request.Status is ConsolidationRunStatus.Failed or ConsolidationRunStatus.Succeeded or ConsolidationRunStatus.Cancelled)
                run.CompletedAtUtc = DateTimeOffset.UtcNow;
            await store.SaveRunAsync(run, ct);
        }

        return TypedResults.Ok();
    }
}

/// <summary>
/// Request body for POST /api/consolidation-runs/{runId}/transition.
/// </summary>
public sealed class ConsolidationRunTransitionRequest
{
    public required ConsolidationRunStatus Status { get; init; }
    public string? Summary { get; init; }
}
