using CodingAgent.Api.Dispatch;
using CodingAgent.Contracts.Auth;
using CodingAgent.Contracts.Ui;
using CodingAgent.Infrastructure.Persistence.Services;
using CodingAgent.Infrastructure.Persistence;
using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;
using Microsoft.EntityFrameworkCore;

namespace CodingAgent.Api.Ui;

/// <summary>
/// Spec 051 2.2a/2.2b/2.3c: run history, the work-item lists, the work-item actions and redispatch. Lists follow D3
/// rule 3 (<see cref="IUserAccess.DemandList"/>): without a project the user needs global read; a
/// scoped user always has a project selected. A run's detail and the actions are checked against
/// the stored project of the run or work item, never one the client sends. The handlers reuse the
/// service surface's, so both surfaces behave the same.
/// </summary>
internal static class UiRunEndpoints
{
    public static RouteGroupBuilder MapUiRunEndpoints(this RouteGroupBuilder group)
    {
        group.MapGet("/runs", GetRuns);
        group.MapGet("/runs/{runId:guid}", GetRun);
        group.MapPost("/runs/{runId:guid}/redispatch", RedispatchRun).RequireAuthorization(UiPolicies.AnyOperator);
        group.MapGet("/work-items/pending", GetPendingWorkItems);
        group.MapGet("/work-items/active", GetActiveWorkItems);
        group.MapPost("/work-items/{id:guid}/cancel", CancelWorkItem).RequireAuthorization(UiPolicies.AnyOperator);
        group.MapPut("/work-items/{id:guid}/priority", SetPriority).RequireAuthorization(UiPolicies.AnyOperator);
        return group;
    }

    /// <summary>GET /api/ui/runs — run history, newest first, with the same filters as the service surface.</summary>
    internal static async Task<IResult> GetRuns(
        [AsParameters] RunHistoryQuery query, IUserAccess access,
        IPipelineRunHistoryService history, IOrchestratorRunService runService,
        IDbContextFactory<PipelineDbContext> dbFactory, CancellationToken ct)
    {
        access.DemandList(query.ProjectId);
        return await PipelineRunEndpoints.GetRunHistory(history, runService, dbFactory, query, ct);
    }

    /// <summary>
    /// GET /api/ui/runs/{runId} — the run and the agent it ran with, as the run recorded it (spec 053 req 8.3); 404 when
    /// the user cannot read the run's project.
    /// </summary>
    internal static async Task<IResult> GetRun(
        Guid runId, IUserAccess access,
        IPipelineRunHistoryService history, IOrchestratorRunService runService, CancellationToken ct)
    {
        var run = await PipelineRunEndpoints.FindRunAsync(runId, history, runService, ct);
        if (run is null)
            return TypedResults.NotFound();
        access.DemandRead(run.ProjectId);

        return TypedResults.Ok(new UiRunDetail(run, new UiRunAgent(run.AgentProfileName, run.ModelName, run.AgentEffort)));
    }

    /// <summary>
    /// POST /api/ui/runs/{runId}/redispatch — queues a new implementation run for the issue of a failed,
    /// cancelled or conflicted implementation run (2.3c, D5). Operator on the run's stored project.
    /// 200 carries the message to show; a run that cannot be re-dispatched is 409 with the reason.
    /// The dispatch runs with <see cref="CancellationToken.None"/>, like the drawers'.
    /// </summary>
    internal static async Task<IResult> RedispatchRun(
        Guid runId, IUserAccess access,
        IPipelineRunHistoryService history, IOrchestratorRunService runService,
        ManualDispatchService dispatch, CancellationToken ct)
    {
        var run = await PipelineRunEndpoints.FindRunAsync(runId, history, runService, ct);
        if (run is null)
            return TypedResults.NotFound();
        access.DemandOperate(run.ProjectId);

        try
        {
            return TypedResults.Ok(await dispatch.RedispatchRunAsync(run, CancellationToken.None));
        }
        catch (DispatchRefusedException ex)
        {
            return TypedResults.Json(ex.Message, statusCode: StatusCodes.Status409Conflict);
        }
    }

    /// <summary>GET /api/ui/work-items/pending — the queue, in dispatch order.</summary>
    internal static async Task<IResult> GetPendingWorkItems(
        IUserAccess access, IDbContextFactory<PipelineDbContext> dbFactory, IPipelineConfigStore configStore,
        CancellationToken ct, int maxResults = 50, string? projectId = null)
    {
        access.DemandList(projectId);
        return await WorkItemQueryEndpoints.GetPendingWorkItems(dbFactory, configStore, maxResults, projectId, ct);
    }

    /// <summary>GET /api/ui/work-items/active — dispatched and running work items.</summary>
    internal static async Task<IResult> GetActiveWorkItems(
        IUserAccess access, IDbContextFactory<PipelineDbContext> dbFactory, IOrchestratorRunService runService,
        CancellationToken ct, int olderThanSeconds = 0, string? projectId = null)
    {
        access.DemandList(projectId);
        return await WorkItemQueryEndpoints.GetActiveWorkItems(olderThanSeconds, dbFactory, runService, projectId, ct);
    }

    /// <summary>
    /// POST /api/ui/work-items/{id}/cancel — cancels a queued item or an in-flight run (a run's ID is
    /// its work item's). 204 when it had already ended.
    /// </summary>
    internal static async Task<IResult> CancelWorkItem(
        Guid id, IUserAccess access, IDbContextFactory<PipelineDbContext> dbFactory,
        WorkItemStatusTransitionService transitions, CancellationToken ct)
    {
        if (!await DemandOperateOnWorkItemAsync(id, access, dbFactory, ct))
            return TypedResults.NotFound();
        return await WorkItemAgentEndpoints.PostStatus(id, new WorkItemStatusRequest { Status = WorkItemStatus.Cancelled }, transitions, ct);
    }

    /// <summary>PUT /api/ui/work-items/{id}/priority — the dispatch weight of a queued item; 409 once it left the queue.</summary>
    internal static async Task<IResult> SetPriority(
        Guid id, UiPriorityRequest request, IUserAccess access, IDbContextFactory<PipelineDbContext> dbFactory,
        WorkItemTransitionService transitions, CancellationToken ct)
    {
        if (!await DemandOperateOnWorkItemAsync(id, access, dbFactory, ct))
            return TypedResults.NotFound();
        return await WorkItemDispatchEndpoints.PostPriorityWeight(
            id, new PriorityWeightRequest { PriorityWeight = request.PriorityWeight }, transitions, ct);
    }

    /// <summary>False when the work item does not exist; throws (404/403) when the user cannot operate on its project.</summary>
    private static async Task<bool> DemandOperateOnWorkItemAsync(
        Guid id, IUserAccess access, IDbContextFactory<PipelineDbContext> dbFactory, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var item = await db.WorkItems.AsNoTracking()
            .Where(w => w.Id == id)
            .Select(w => new { w.ProjectId })
            .FirstOrDefaultAsync(ct);
        if (item is null)
            return false;
        access.DemandOperate(ProjectIds.Normalize(item.ProjectId));
        return true;
    }
}
