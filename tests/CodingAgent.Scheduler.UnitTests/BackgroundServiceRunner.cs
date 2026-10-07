using AwesomeAssertions;
using Microsoft.Extensions.Hosting;

namespace CodingAgent.Scheduler.UnitTests;

/// <summary>
/// Runs a timer-driven <see cref="BackgroundService"/> until a mock signals the call a test asserts on.
/// </summary>
/// <remarks>
/// Running the service for a fixed window and then asserting fails when the test host stalls past
/// the window before the first tick (loaded CI runners, resource-limited agent pods): the stop
/// cancels the service before it has done anything. Waiting on the signal only lengthens the run;
/// the 30s bound is a hang detector.
/// </remarks>
internal static class BackgroundServiceRunner
{
    public static async Task RunUntilAsync(BackgroundService service, Task signal, string because)
    {
        await service.StartAsync(CancellationToken.None);
        try
        {
            var completed = await Task.WhenAny(signal, Task.Delay(TimeSpan.FromSeconds(30)));
            completed.Should().BeSameAs(signal, because);
        }
        finally
        {
            // Stop with a timeout — BackgroundService.StopAsync can hang if ExecuteAsync doesn't respond
            using var stopCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try { await service.StopAsync(stopCts.Token); } catch { /* timeout or cancellation — ignore */ }
            service.Dispose();
        }
    }
}

/// <summary>
/// Completes <see cref="Reached"/> once <see cref="Hit"/> has been called <c>count</c> times.
/// Hook <see cref="Hit"/> into a mock callback; waiting for the second call of a once-per-tick
/// mock proves the first tick ran to completion. Thread-safe.
/// </summary>
internal sealed class CallSignal(int count = 1)
{
    private readonly TaskCompletionSource _reached = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _calls;

    public Task Reached => _reached.Task;

    public void Hit()
    {
        if (Interlocked.Increment(ref _calls) >= count)
            _reached.TrySetResult();
    }
}
