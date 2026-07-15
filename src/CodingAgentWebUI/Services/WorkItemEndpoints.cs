using System.Text.Json;
using CodingAgentWebUI.Infrastructure.Persistence;
using CodingAgentWebUI.Infrastructure.Persistence.Entities;
using CodingAgentWebUI.Infrastructure.Persistence.Services;
using CodingAgentWebUI.Orchestration.Dispatch;
using CodingAgentWebUI.Orchestration.Telemetry;
using CodingAgentWebUI.Pipeline;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace CodingAgentWebUI.Services;

/// <summary>
/// Request body for POST /api/work-items/{id}/status.
/// </summary>
public sealed class WorkItemStatusRequest
{
    public required WorkItemStatus Status { get; init; }
    public string? AgentId { get; init; }
    public string? Result { get; init; }
    public string? ErrorMessage { get; init; }
    public string? FailureReason { get; init; }
}

/// <summary>
/// Minimal API endpoints for Work Item HTTP API.
/// Agents call these to fetch assignments and report status updates.
/// </summary>
public static class WorkItemEndpoints
{
    /// <summary>
    /// Maps work item endpoints onto the application endpoint route builder.
    /// </summary>
    public static void MapWorkItemEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/work-items")
            .RequireAuthorization("AgentApiKey");

        group.MapGet("/{id:guid}/assignment", GetAssignment);
        group.MapPost("/{id:guid}/status",
            async (Guid id, [FromBody] WorkItemStatusRequest request,
                   [FromServices] WorkItemTransitionService transitionService,
                   [FromServices] IOrchestratorRunService runService,
                   HttpContext httpContext) =>
                await PostStatus(id, request, transitionService, runService,
                    httpContext.RequestServices.GetService<IDbContextFactory<PipelineDbContext>>()))
            .WithMetadata(new Microsoft.AspNetCore.Mvc.RequestSizeLimitAttribute(1_048_576)); // 1MB limit
    }

    /// <summary>
    /// GET /api/work-items/{id}/assignment
    /// Loads Payload JSONB, deserializes, maps to <see cref="JobAssignmentMessage"/> via
    /// <see cref="DbWorkDistributorBase.BuildJobAssignmentMessage"/>.
    /// Returns 200 with assignment, 404 if not found, 410 if terminal status.
    /// </summary>
    internal static async Task<IResult> GetAssignment(
        Guid id,
        IDbContextFactory<PipelineDbContext> dbFactory)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var item = await db.WorkItems
            .AsNoTracking()
            .Where(w => w.Id == id)
            .Select(w => new { w.Status, w.Payload })
            .FirstOrDefaultAsync();

        if (item is null)
            return TypedResults.NotFound();

        // Terminal statuses → 410 Gone
        if (item.Status is WorkItemStatus.Succeeded or WorkItemStatus.Failed or WorkItemStatus.Cancelled)
            return TypedResults.StatusCode(410);

        if (item.Payload is null)
            return TypedResults.NotFound();

        var request = JsonSerializer.Deserialize<JobDistributionRequest>(item.Payload, PipelineJsonOptions.Default);
        if (request is null)
            return TypedResults.NotFound();

        var message = Orchestration.Dispatch.DbWorkDistributorBase.BuildJobAssignmentMessage(id, request);
        return TypedResults.Ok(message);
    }

    /// <summary>
    /// POST /api/work-items/{id}/status
    /// Validates transition via WorkItemTransitionService, updates in-memory state.
    /// Returns 200, 400 (invalid transition), or 404.
    /// </summary>
    internal static async Task<IResult> PostStatus(
        Guid id,
        WorkItemStatusRequest request,
        WorkItemTransitionService transitionService,
        IOrchestratorRunService runService,
        IDbContextFactory<PipelineDbContext>? dbFactory = null)
    {
        var success = await transitionService.TransitionAsync(
            id, request.Status,
            mutate: entity =>
            {
                if (request.AgentId is not null)
                    entity.AssignedAgentId = request.AgentId;

                if (request.ErrorMessage is not null)
                    entity.ErrorMessage = request.ErrorMessage;
                else if (request.Status == WorkItemStatus.Failed)
                    entity.ErrorMessage = "Job failed without specific error information";

                if (request.Result is not null)
                    entity.Result = request.Result;

                // Set FailureReason enum from string when status is Failed
                if (request.Status == WorkItemStatus.Failed)
                {
                    if (request.FailureReason is not null
                        && Enum.TryParse<Pipeline.Models.FailureReason>(request.FailureReason, ignoreCase: true, out var parsedReason))
                    {
                        entity.FailureReason ??= parsedReason;
                    }
                    else
                    {
                        entity.FailureReason ??= Pipeline.Models.FailureReason.AgentError;
                    }
                }

                // Set CompletedAt for terminal statuses
                if (request.Status is WorkItemStatus.Succeeded or WorkItemStatus.Failed or WorkItemStatus.Cancelled)
                    entity.CompletedAt = DateTimeOffset.UtcNow;
            });

        if (!success)
        {
            // TransitionService returns false for both "not found" and "invalid transition".
            // Check existence to distinguish the two.
            if (dbFactory is not null)
            {
                await using var db = await dbFactory.CreateDbContextAsync();
                var exists = await db.WorkItems.AnyAsync(w => w.Id == id);
                if (!exists)
                    return TypedResults.NotFound();
            }

            return TypedResults.BadRequest("Invalid status transition");
        }

        // Emit structured terminal-status log and metrics (Req 10.3)
        if (request.Status is WorkItemStatus.Succeeded or WorkItemStatus.Failed or WorkItemStatus.Cancelled)
        {
            TimeSpan? duration = null;
            if (dbFactory is not null)
            {
                await using var db = await dbFactory.CreateDbContextAsync();
                var item = await db.WorkItems.AsNoTracking()
                    .Where(w => w.Id == id)
                    .Select(w => new { w.DispatchedAt, w.CompletedAt })
                    .FirstOrDefaultAsync();
                if (item?.DispatchedAt is not null && item.CompletedAt is not null)
                    duration = item.CompletedAt.Value - item.DispatchedAt.Value;
            }

            WorkDistributionTelemetry.LogTerminalStatus(
                id, request.Status, duration, request.AgentId, failureReason: null);
        }

        return TypedResults.Ok();
    }
}
