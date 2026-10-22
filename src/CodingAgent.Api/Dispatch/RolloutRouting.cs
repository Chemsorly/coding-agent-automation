using System.Text.Json;
using CodingAgent.Infrastructure.Persistence;
using CodingAgent.Pipeline;
using CodingAgent.Pipeline.Models;
using CodingAgent.Pipeline.Telemetry;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Serilog;

namespace CodingAgent.Api.Dispatch;

/// <summary>
/// Spec 053 DD10, design "Dispatcher → Rollout routing": during a rolling deploy, a previous-release API replica
/// stores a current-release caller's request without an agent selector or route, because the caller no longer sends
/// them. Before such a Pending item is dispatched, it is routed here with the API's router (route source
/// <c>dispatch</c>), and its selector and payload are written under the row's concurrency token. A refusal fails the
/// item with the reason; nothing picks an unrouted profile (D15).
/// </summary>
public sealed class RolloutRouting(
    IDbContextFactory<PipelineDbContext> dbFactory,
    WorkItemRouter router,
    WorkItemStatusTransitionService transitions)
{
    internal const string NoRouteReason = "no_route";

    /// <summary>
    /// Routes the selector-less Pending item <paramref name="id"/>.
    /// </summary>
    /// <returns>
    /// The selector to dispatch under; or an early result: 200 <c>no_route</c> when the route was refused (the item is
    /// Failed), 200 <c>not_pending</c> when the item moved on, 503 on an unexpected error. Never a 500, because any other
    /// status aborts the Scheduler's whole dispatch cycle (V9).
    /// </returns>
    internal async Task<(string? Selector, IResult? Early)> RouteAsync(Guid id, CancellationToken ct)
    {
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            var entity = await db.WorkItems.FirstOrDefaultAsync(w => w.Id == id, ct);
            if (entity is null)
                return (null, TypedResults.NotFound());
            if (entity.Status != WorkItemStatus.Pending)
                return (null, NotPending());
            if (!string.IsNullOrEmpty(entity.AgentSelector))
                return (entity.AgentSelector, null);

            if (!WorkItemPayload.TryDeserialize(entity.Payload, out var request) || request is null || request.Route is not null)
            {
                // Nothing to route from; the dispatcher defers it as no_template, as before.
                Log.Warning("Rollout routing: work item {WorkItemId} has no selector and no routable payload", id);
                return ("", null);
            }

            var routed = await router.RouteAsync(request, PipelineTelemetry.RoutingStages.Dispatch, ct, AgentRouteSource.Dispatch);
            if (routed.IsRefused)
            {
                await transitions.TransitionAsync(id, new WorkItemStatusRequest
                {
                    Status = WorkItemStatus.Failed,
                    ErrorMessage = routed.RefusalText,
                    FailureReason = nameof(FailureReason.InfrastructureFailure),
                }, ct);
                WorkDistributionTelemetry.RecordDispatchAttempt("deferred", NoRouteReason);
                return (null, TypedResults.Ok(new DispatchPendingResponse(false, NoRouteReason)));
            }

            entity.AgentSelector = routed.Request!.AgentSelector;
            entity.Payload = JsonSerializer.Serialize(
                WorkItemDispatchEndpoints.BuildMinimalPayload(routed.Request), PipelineJsonOptions.Default);
            await db.SaveChangesAsync(ct);
            Log.Information(
                "Rollout routing: work item {WorkItemId} stored without a selector now runs on job template {AgentSelector} ({RouteSource})",
                id, entity.AgentSelector, AgentRouteLabels.Source(AgentRouteSource.Dispatch));
            return (entity.AgentSelector, null);
        }
        catch (DbUpdateConcurrencyException)
        {
            // Another dispatcher routed or moved the item meanwhile; the next cycle reads it again.
            return (null, NotPending());
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Error(ex, "Rollout routing of work item {WorkItemId} failed; returning 503", id);
            return (null, TypedResults.StatusCode(StatusCodes.Status503ServiceUnavailable));
        }
    }

    private static Ok<DispatchPendingResponse> NotPending()
    {
        WorkDistributionTelemetry.RecordDispatchAttempt("deferred", "not_pending");
        return TypedResults.Ok(new DispatchPendingResponse(false, "not_pending"));
    }
}
