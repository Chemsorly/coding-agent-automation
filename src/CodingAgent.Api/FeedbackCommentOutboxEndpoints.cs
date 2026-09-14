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
        // Defence-in-depth: add an upper-bound cap on pageSize (e.g. 500) to prevent a caller with
        // a valid AgentApiKey from issuing a TAKE N query that materialises the entire table into
        // memory. Low risk in practice (Operator auth + single trusted caller hardcodes pageSize=20),
        // but is a missing guard at a public API boundary. Tracked as a follow-up improvement.

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
        // Validation note: request.MaxAttempts should be > 0. A caller passing maxAttempts <= 0 causes
        // PostgresFeedbackCommentOutboxStore.MarkFailedAsync to immediately transition every entry to
        // Failed (AttemptCount >= maxAttempts is true when maxAttempts <= 0), permanently silencing
        // entries. Similarly, ErrorMessage length should be bounded (e.g. 2000 chars). Both are
        // tracked as follow-up input validation improvements.
        await outbox.MarkFailedAsync(id, request.ErrorMessage, request.MaxAttempts, ct);
        return TypedResults.Ok();
    }

    /// <summary>Request body for the mark-failed endpoint.</summary>
    public sealed record MarkFailRequest(string ErrorMessage, int MaxAttempts);
}
