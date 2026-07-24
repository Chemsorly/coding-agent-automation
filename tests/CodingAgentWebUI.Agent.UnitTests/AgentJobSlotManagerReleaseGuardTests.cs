using AwesomeAssertions;
using CodingAgentWebUI.Agent;

namespace CodingAgentWebUI.Agent.UnitTests;

/// <summary>
/// Tests for the exactly-once release guard in <see cref="AgentJobSlotManager.ReleaseJobSlotAndSignalReadyAsync"/>.
/// Verifies that the atomic guard (Interlocked.CompareExchange on <c>_jobReleased</c>) ensures
/// <c>signalReady</c> is invoked at most once per job, regardless of concurrent or sequential calls.
/// </summary>
public class AgentJobSlotManagerReleaseGuardTests
{
    // ── Concurrent release — exactly one signal ─────────────────────────

    [Fact]
    public async Task ConcurrentRelease_ResultsInExactlyOneSignalReady()
    {
        // Arrange: create a slot manager with a callback that counts invocations
        var signalCount = 0;
        var slotManager = new AgentJobSlotManager(() =>
        {
            Interlocked.Increment(ref signalCount);
            return Task.CompletedTask;
        });
        slotManager.TryAcquireJobSlot("job-1", out _);

        // Act: spawn many concurrent release attempts
        const int concurrency = 50;
        var barrier = new Barrier(concurrency);
        var tasks = Enumerable.Range(0, concurrency).Select(_ => Task.Run(async () =>
        {
            barrier.SignalAndWait(); // Ensure all tasks start at the same time
            await slotManager.ReleaseJobSlotAndSignalReadyAsync();
        })).ToArray();

        await Task.WhenAll(tasks);

        // Assert: signal was called exactly once
        signalCount.Should().Be(1);
    }

    // ── Sequential double call — second is no-op ────────────────────────

    [Fact]
    public async Task ReleaseJobSlotAndSignalReadyAsync_CalledTwice_SecondCallIsNoOp()
    {
        var signalCount = 0;
        var slotManager = new AgentJobSlotManager(() =>
        {
            Interlocked.Increment(ref signalCount);
            return Task.CompletedTask;
        });
        slotManager.TryAcquireJobSlot("job-1", out _);

        // Act: call release twice sequentially
        await slotManager.ReleaseJobSlotAndSignalReadyAsync();
        await slotManager.ReleaseJobSlotAndSignalReadyAsync();

        // Assert: signal was called exactly once
        signalCount.Should().Be(1);
    }

    // ── Guard resets on new job acquisition ──────────────────────────────

    [Fact]
    public async Task TryAcquireJobSlot_ResetsReleaseGuard()
    {
        var signalCount = 0;
        var slotManager = new AgentJobSlotManager(() =>
        {
            Interlocked.Increment(ref signalCount);
            return Task.CompletedTask;
        });

        // First job: acquire and release
        slotManager.TryAcquireJobSlot("job-1", out _);
        await slotManager.ReleaseJobSlotAndSignalReadyAsync();
        signalCount.Should().Be(1);

        // Second job: acquire resets the guard, so release signals again
        slotManager.TryAcquireJobSlot("job-2", out _);
        await slotManager.ReleaseJobSlotAndSignalReadyAsync();
        signalCount.Should().Be(2);
    }

    // ── ForceReleaseJobSlot prevents subsequent signal ───────────────────

    [Fact]
    public async Task ForceReleaseJobSlot_PreventsSubsequentSignalReady()
    {
        var signalCount = 0;
        var slotManager = new AgentJobSlotManager(() =>
        {
            Interlocked.Increment(ref signalCount);
            return Task.CompletedTask;
        });
        slotManager.TryAcquireJobSlot("job-1", out _);

        // Act: force release (no signal), then attempt normal release
        slotManager.ForceReleaseJobSlot();
        await slotManager.ReleaseJobSlotAndSignalReadyAsync();

        // Assert: signal was never called — ForceReleaseJobSlot set the guard
        signalCount.Should().Be(0);
    }

    // ── Stress test: multiple iterations to increase race coverage ───────

    [Fact]
    public async Task ConcurrentRelease_MultipleIterations_AlwaysExactlyOneSignal()
    {
        for (int iteration = 0; iteration < 100; iteration++)
        {
            var signalCount = 0;
            var slotManager = new AgentJobSlotManager(() =>
            {
                Interlocked.Increment(ref signalCount);
                return Task.CompletedTask;
            });
            slotManager.TryAcquireJobSlot($"job-{iteration}", out _);

            var barrier = new Barrier(10);
            var tasks = Enumerable.Range(0, 10).Select(_ => Task.Run(async () =>
            {
                barrier.SignalAndWait();
                await slotManager.ReleaseJobSlotAndSignalReadyAsync();
            })).ToArray();

            await Task.WhenAll(tasks);

            signalCount.Should().Be(1, $"iteration {iteration} should produce exactly one signal");
        }
    }
}
