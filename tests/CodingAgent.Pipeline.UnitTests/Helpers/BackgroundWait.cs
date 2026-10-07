using AwesomeAssertions;
using CodingAgent.Pipeline.Services;

namespace CodingAgent.Pipeline.UnitTests.Helpers;

/// <summary>
/// Waits for tests that start background work (the <see cref="PipelineLoopService"/> loop, a
/// pipeline run) and must observe it reaching a state before asserting.
/// </summary>
/// <remarks>
/// The background work and the test share the thread pool. When the test process stalls (a loaded
/// CI runner, or other tests blocking pool threads), a short wall-clock deadline can expire before
/// the background work's queued wake-up runs, and the test fails although the code works. These
/// waits complete on a signal from the code under test or a mock instead, and their
/// <see cref="Timeout"/> only matters when that code is genuinely broken. Every wait asserts that it
/// was signalled, so a timeout fails with the caller's reason instead of letting the test assert
/// against a state it never reached.
/// </remarks>
internal static class BackgroundWait
{
    /// <summary>Safety bound for every wait. Working code signals in milliseconds.</summary>
    internal static readonly TimeSpan Timeout = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Completes when <paramref name="condition"/> holds, evaluated now and on every
    /// <see cref="PipelineLoopService.OnChange"/>. The handler is subscribed before this method
    /// first yields, so callers can start the wait, then trigger the change, then await it. It also
    /// catches states that last only briefly, because the handler runs on the loop's thread right
    /// after the change.
    /// </summary>
    internal static Task UntilAsync(PipelineLoopService svc, Func<bool> condition, string because) =>
        UntilAsync(h => svc.OnChange += h, h => svc.OnChange -= h, condition, because);

    /// <summary>
    /// Completes when <paramref name="condition"/> holds, evaluated now and on every notification of
    /// the change event that <paramref name="subscribe"/> attaches to.
    /// </summary>
    internal static async Task UntilAsync(
        Action<Action> subscribe, Action<Action> unsubscribe, Func<bool> condition, string because)
    {
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void Check()
        {
            if (condition())
                reached.TrySetResult();
        }

        subscribe(Check);
        try
        {
            Check();
            await SignalledAsync(reached.Task, because);
        }
        finally
        {
            unsubscribe(Check);
        }
    }

    /// <summary>Completes once the loop finishes a cycle and shows "Cycle complete".</summary>
    internal static Task CycleCompleteAsync(PipelineLoopService svc) =>
        UntilAsync(
            svc,
            () => svc.StatusMessage.Contains("Cycle complete", StringComparison.OrdinalIgnoreCase),
            "the loop should complete a poll cycle");

    /// <summary>
    /// Completes once the loop has stopped. The notification comes from the end of the loop's
    /// cleanup, after the provider cache was disposed.
    /// </summary>
    internal static Task StoppedAsync(PipelineLoopService svc) =>
        UntilAsync(svc, () => !svc.IsLoopActive, "the loop should stop");

    /// <summary>Completes when <paramref name="signal"/> (typically set by a mock) completes.</summary>
    internal static async Task SignalledAsync(Task signal, string because)
    {
        try
        {
            await signal.WaitAsync(Timeout);
        }
        catch (TimeoutException)
        {
            // Asserted below so the failure carries the caller's reason.
        }

        signal.IsCompleted.Should().BeTrue($"{because} (waited {Timeout.TotalSeconds:0}s)");
        await signal;
    }

    /// <summary>
    /// Polls <paramref name="condition"/> until it holds. Use only for state that has no change
    /// notification and no natural signal (for example a counter in a mock shared by many calls).
    /// </summary>
    internal static async Task PollUntilAsync(Func<bool> condition, string because)
    {
        var deadline = DateTime.UtcNow + Timeout;
        while (!condition() && DateTime.UtcNow < deadline)
            await Task.Delay(20);

        condition().Should().BeTrue($"{because} (waited {Timeout.TotalSeconds:0}s)");
    }
}
