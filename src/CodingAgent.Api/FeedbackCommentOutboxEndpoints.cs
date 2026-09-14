using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;

namespace CodingAgent.Api;

/// <summary>
/// Stateless API endpoints for the feedback-comment durable outbox.
/// Consumed exclusively by the leader-gated FeedbackCommentRelayService in CodingAgent.Scheduler.
/// All endpoints require operator-level (AgentApiKey) authentication.
/// </summary>
public static class FeedbackCommentOutboxEndpoints
{
    public static void MapFeedbackCommentOutboxEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/feedback-comment-outbox")
            .RequireAuthorization(ApiAuthPolicies.Operator);

        group.MapGet("/pending", GetPending);
        group.MapPost("/{id:guid}/complete", MarkComplete);
        group.MapPost("/{id:guid}/fail", MarkFail);
    }

    internal static async Task<IResult> GetPending(
        [FromQuery] int maxAttempts,
        [FromQuery] int pageSize,
        [FromServices] IFeedbackCommentOutbox outbox,
        CancellationToken ct)
    {
        if (maxAttempts <= 0) maxAttempts = 5;
        if (pageSize <= 0) pageSize = 20;
        // TODO [WARNING]: Add an upper-bound cap on pageSize (e.g. 500) to prevent a caller with
        // a valid AgentApiKey from issuing a TAKE N query that materialises the entire table into
        // memory in a single request. Low risk in practice (Operator auth + single trusted caller
        // that hardcodes pageSize=20), but is a missing defence-in-depth guard at a public API boundary.

        var entries = await outbox.GetPendingAsync(maxAttempts, pageSize, ct);
        return TypedResults.Ok(entries);
    }

    internal static async Task<IResult> MarkComplete(
        Guid id,
        [FromServices] IFeedbackCommentOutbox outbox,
        CancellationToken ct)
    {
        await outbox.MarkCompletedAsync(id, ct);
        return TypedResults.Ok();
    }

    internal static async Task<IResult> MarkFail(
        Guid id,
        [FromBody] MarkFailRequest request,
        [FromServices] IFeedbackCommentOutbox outbox,
        CancellationToken ct)
    {
        // TODO [WARNING]: Validate request.MaxAttempts > 0. A caller passing maxAttempts <= 0 causes
        // PostgresFeedbackCommentOutboxStore.MarkFailedAsync to immediately transition every entry to
        // Failed (AttemptCount >= maxAttempts is true when maxAttempts <= 0), permanently silencing
        // entries without delivery. Also consider capping ErrorMessage length (e.g. 2000 chars).
        await outbox.MarkFailedAsync(id, request.ErrorMessage, request.MaxAttempts, ct);
        return TypedResults.Ok();
    }

    /// <summary>Request body for the mark-failed endpoint.</summary>
    public sealed record MarkFailRequest(string ErrorMessage, int MaxAttempts);
}
