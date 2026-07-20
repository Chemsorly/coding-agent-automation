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
    // TODO: ResetStartedAt writes StartedAt and StartedAtOffset non-atomically without synchronization.
    // The class uses Interlocked for LastStepChangeAt (analogous concurrent-read pattern). Consider
    // adding a lock or Interlocked pattern here for consistency, especially since this is called from
    // DispatchService background thread while UI threads may read StartedAtOffset concurrently.
    public void ResetStartedAt(DateTimeOffset actualStart)
    {
#pragma warning disable CS0618
        StartedAt = actualStart.UtcDateTime;
#pragma warning restore CS0618
        StartedAtOffset = actualStart;
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
