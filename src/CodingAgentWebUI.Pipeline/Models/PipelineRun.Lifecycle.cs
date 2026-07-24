using System.Threading;

namespace CodingAgentWebUI.Pipeline.Models;

public sealed partial class PipelineRun
{
    /// <summary>Atomically sets both <see cref="CompletedAt"/> and <see cref="CompletedAtOffset"/> to the current UTC time.</summary>
    public void MarkCompleted()
    {
        var now = DateTimeOffset.UtcNow;
#pragma warning disable CS0618
        CompletedAt = now.UtcDateTime;
#pragma warning restore CS0618
        CompletedAtOffset = now;
    }

    /// <summary>Atomically sets both <see cref="CompletedAt"/> and <see cref="CompletedAtOffset"/> from the provided timestamp.</summary>
    public void MarkCompleted(DateTimeOffset timestamp)
    {
#pragma warning disable CS0618
        CompletedAt = timestamp.UtcDateTime;
#pragma warning restore CS0618
        CompletedAtOffset = timestamp;
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
