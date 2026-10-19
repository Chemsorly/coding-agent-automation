using System.Threading;

namespace CodingAgent.Pipeline.Models;

public sealed partial class PipelineRun
{
    /// <summary>
    /// Sets <see cref="CompletedAt"/> and <see cref="CompletedAtOffset"/> to the current UTC time.
    /// Idempotent: the first writer wins and subsequent calls are no-ops.
    /// </summary>
    /// <remarks>
    /// Thread-safety: The <see cref="CompletedAtOffset"/> check and both writes are guarded by
    /// <c>_completedAtLock</c> so that no reader can observe one field updated without the other,
    /// and only one timestamp snapshot can win the race.
    /// </remarks>
    public void MarkCompleted()
    {
        lock (_completedAtLock)
        {
            if (CompletedAtOffset.HasValue)
                return; // Already completed — preserve the first timestamp

            var now = DateTimeOffset.UtcNow;
#pragma warning disable CS0618
            CompletedAt = now.UtcDateTime;
#pragma warning restore CS0618
            CompletedAtOffset = now;
        }
    }

    /// <summary>
    /// Sets <see cref="CompletedAt"/> and <see cref="CompletedAtOffset"/> from the provided timestamp.
    /// Idempotent: the first writer wins and subsequent calls are no-ops.
    /// </summary>
    /// <remarks>
    /// Thread-safety: The <see cref="CompletedAtOffset"/> check and both writes are guarded by
    /// <c>_completedAtLock</c> so that no reader can observe one field updated without the other,
    /// and only one timestamp snapshot can win the race.
    /// </remarks>
    public void MarkCompleted(DateTimeOffset timestamp)
    {
        lock (_completedAtLock)
        {
            if (CompletedAtOffset.HasValue)
                return; // Already completed — preserve the first timestamp

#pragma warning disable CS0618
            CompletedAt = timestamp.UtcDateTime;
#pragma warning restore CS0618
            CompletedAtOffset = timestamp;
        }
    }

    /// <summary>
    /// Sets the run's completion time from the agent's final report, replacing an earlier stamp
    /// such as the one taken when the pull request was finalized.
    /// </summary>
    /// <remarks>
    /// Thread-safety: Both writes are guarded by <c>_completedAtLock</c> so that no reader can
    /// observe the new <see cref="CompletedAt"/> with the stale <see cref="CompletedAtOffset"/>
    /// (or vice versa).
    /// </remarks>
    public void SetCompletedAt(DateTimeOffset timestamp)
    {
        lock (_completedAtLock)
        {
#pragma warning disable CS0618
            CompletedAt = timestamp.UtcDateTime;
#pragma warning restore CS0618
            CompletedAtOffset = timestamp;
        }
    }

    /// <summary>
    /// Resets StartedAt to the actual agent dispatch time. Called when a queued
    /// WorkItem transitions Pending→Dispatched, replacing the preparation-time
    /// timestamp with the true agent start time.
    /// </summary>
    /// <remarks>
    /// Thread-safety: Both writes are guarded by <see cref="_startedAtLock"/> so that
    /// no reader can observe the new StartedAt with the stale StartedAtOffset (or vice versa).
    /// </remarks>
    public void ResetStartedAt(DateTimeOffset actualStart)
    {
        lock (_startedAtLock)
        {
#pragma warning disable CS0618
            StartedAt = actualStart.UtcDateTime;
#pragma warning restore CS0618
            StartedAtOffset = actualStart;
        }
    }

    /// <summary>Atomically adds to the code review severity counters. Use when accumulating counts from concurrent review agents.</summary>
    public void AddCodeReviewCounts(int critical, int warning, int suggestion)
    {
        Interlocked.Add(ref _codeReviewCriticalCount, critical);
        Interlocked.Add(ref _codeReviewWarningCount, warning);
        Interlocked.Add(ref _codeReviewSuggestionCount, suggestion);
    }

    /// <summary>Atomically replaces the code review severity counters. Use when setting absolute values from a completion payload.</summary>
    public void SetCodeReviewCounts(int critical, int warning, int suggestion)
    {
        Interlocked.Exchange(ref _codeReviewCriticalCount, critical);
        Interlocked.Exchange(ref _codeReviewWarningCount, warning);
        Interlocked.Exchange(ref _codeReviewSuggestionCount, suggestion);
    }
}
