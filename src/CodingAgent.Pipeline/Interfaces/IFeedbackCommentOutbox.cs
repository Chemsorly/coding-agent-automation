using CodingAgent.Pipeline.Models;

namespace CodingAgent.Pipeline.Interfaces;

/// <summary>
/// Durable outbox for feedback comments that need to be posted after pod shutdown.
/// Enqueued before the ApplicationStopping-linked CancellationTokenSource is created in
/// PostCompletionBookkeepingAsync, so the row survives graceful shutdown even if the
/// inline fast-path comment post is cancelled.
/// The unique index on RunId makes repeated calls idempotent (ON CONFLICT DO NOTHING).
/// </summary>
public interface IFeedbackCommentOutbox
{
    /// <summary>
    /// Persists a feedback comment entry for eventual delivery.
    /// Idempotent: duplicate RunId is silently ignored (ON CONFLICT DO NOTHING).
    /// Always called with <see cref="CancellationToken.None"/> so it survives ApplicationStopping.
    /// </summary>
    Task EnqueueAsync(FeedbackCommentOutboxEntry entry, CancellationToken ct = default);

    /// <summary>
    /// Returns up to <paramref name="pageSize"/> pending entries whose attempt count is below
    /// <paramref name="maxAttempts"/>, ordered by creation time (oldest first).
    /// </summary>
    Task<IReadOnlyList<FeedbackCommentOutboxEntry>> GetPendingAsync(
        int maxAttempts,
        int pageSize,
        CancellationToken ct = default);

    /// <summary>Marks an entry as successfully delivered.</summary>
    Task MarkCompletedAsync(Guid id, CancellationToken ct = default);

    /// <summary>
    /// Increments the attempt count and records the error.
    /// If the new count reaches <paramref name="maxAttempts"/>, transitions the row to Failed
    /// (so it is excluded from future sweeps); otherwise leaves it Pending.
    /// </summary>
    Task MarkFailedAsync(Guid id, string errorMessage, int maxAttempts, CancellationToken ct = default);
}
