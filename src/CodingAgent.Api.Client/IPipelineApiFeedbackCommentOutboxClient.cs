using CodingAgent.Pipeline.Models;

namespace CodingAgent.Api.Client;

/// <summary>
/// Typed HTTP client for the /api/feedback-comment-outbox endpoint group.
/// Consumed by <c>FeedbackCommentRelayService</c> in CodingAgent.Scheduler.
/// </summary>
public interface IPipelineApiFeedbackCommentOutboxClient
{
    /// <summary>Returns pending outbox entries whose attempt count is below <paramref name="maxAttempts"/>.</summary>
    Task<IReadOnlyList<FeedbackCommentOutboxEntry>> GetPendingAsync(
        int maxAttempts,
        int pageSize,
        CancellationToken ct = default);

    /// <summary>Marks an outbox entry as successfully delivered.</summary>
    Task MarkCompletedAsync(Guid id, CancellationToken ct = default);

    /// <summary>Records a delivery failure and increments the attempt count.</summary>
    Task MarkFailedAsync(Guid id, string errorMessage, int maxAttempts, CancellationToken ct = default);
}
