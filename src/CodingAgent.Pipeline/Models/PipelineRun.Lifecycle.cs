using System.Threading;

namespace CodingAgent.Pipeline.Models;

public sealed partial class PipelineRun
{
    /// <summary>Atomically sets both <see cref="CompletedAt"/> and <see cref="CompletedAtOffset"/> to the current UTC time.</summary>
    /// <remarks>Idempotent: if the run is already completed, the existing timestamps are preserved and the call is a no-op.</remarks>
    // TODO [WARNING] (DotNetSpecialist): This check-then-act on CompletedAtOffset.HasValue is not thread-safe. Two threads
    // racing into MarkCompleted() can both observe HasValue == false, both pass the guard, and both proceed to write
    // CompletedAt/CompletedAtOffset with different DateTimeOffset.UtcNow snapshots (the later write wins, causing
    // the same timestamp-skew this fix intended to prevent). CompletedAtOffset has no synchronisation guard unlike
    // StartedAt/StartedAtOffset which use _startedAtLock. Consider adding a dedicated lock (e.g. _completedAtLock)
    // mirroring the ResetStartedAt pattern to make the check-then-set truly atomic.
    public void MarkCompleted()
    {
        if (CompletedAtOffset.HasValue)
            return; // Already completed — preserve the first timestamp

        var now = DateTimeOffset.UtcNow;
#pragma warning disable CS0618
        CompletedAt = now.UtcDateTime;
#pragma warning restore CS0618
        CompletedAtOffset = now;
    }

    /// <summary>Atomically sets both <see cref="CompletedAt"/> and <see cref="CompletedAtOffset"/> from the provided timestamp.</summary>
    /// <remarks>Idempotent: if the run is already completed, the existing timestamps are preserved and the call is a no-op.</remarks>
    // TODO [WARNING] (DotNetSpecialist): Same check-then-act race as the parameterless overload above — two threads can both
    // observe HasValue == false and proceed to write, with the later write winning. CompletedAtOffset is a plain
    // auto-property with no synchronisation guard. PipelineRun is a shared-state object and MarkCompleted is public,
    // so this race is reachable from RunLifecycleManager, PipelineRunLifecycleService, and LocalPipelineExecutor where
    // concurrent access is more likely. Add a dedicated lock mirroring the _startedAtLock / ResetStartedAt pattern.
    public void MarkCompleted(DateTimeOffset timestamp)
    {
        if (CompletedAtOffset.HasValue)
            return; // Already completed — preserve the first timestamp

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
