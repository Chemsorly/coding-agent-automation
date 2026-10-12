using CodingAgent.Contracts.Ui;
using CodingAgent.Infrastructure.Persistence;
using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;
using Microsoft.EntityFrameworkCore;

namespace CodingAgent.Api.Ui;

/// <summary>
/// Spec 051 2.2a: run history and the work-item lists, for every role. Lists follow D3 rule 3
/// (<see cref="IUserAccess.DemandList"/>): without a project the user needs global read; a scoped
/// user always has a project selected. A run's detail is checked against the run's own project.
/// The handlers reuse the service surface's, so both surfaces return the same data.
/// </summary>
internal static class UiRunEndpoints
{
    public static RouteGroupBuilder MapUiRunEndpoints(this RouteGroupBuilder group)
    {
        group.MapGet("/runs", GetRuns);
        group.MapGet("/runs/{runId:guid}", GetRun);
        group.MapGet("/work-items/pending", GetPendingWorkItems);
        group.MapGet("/work-items/active", GetActiveWorkItems);
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

    /// <summary>GET /api/ui/runs/{runId} — the run and its agent provider's name; 404 when the user cannot read the run's project.</summary>
    internal static async Task<IResult> GetRun(
        Guid runId, IUserAccess access,
        IPipelineRunHistoryService history, IOrchestratorRunService runService,
        IProviderConfigStore providers, CancellationToken ct)
    {
        var run = await PipelineRunEndpoints.FindRunAsync(runId, history, runService, ct);
        if (run is null)
            return TypedResults.NotFound();
        access.DemandRead(run.ProjectId);

        var agentProvider = run.AgentProviderConfigId is { Length: > 0 } providerId
            ? await providers.GetProviderConfigByIdAsync(providerId, ProviderKind.Agent, ct)
            : null;
        return TypedResults.Ok(new UiRunDetail(run, agentProvider?.DisplayName));
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
}
