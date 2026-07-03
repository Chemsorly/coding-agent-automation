using AwesomeAssertions;
using CodingAgentWebUI.Agent;
using CodingAgentWebUI.Pipeline.Models;

namespace CodingAgentWebUI.Agent.UnitTests;

/// <summary>
/// Unit tests for <see cref="CriticalMessageBuffer"/> — bounded buffer mechanics,
/// overflow policy, drain ordering, and thread safety.
/// </summary>
public class CriticalMessageBufferTests
{
    // ── Basic Operations ─────────────────────────────────────────────────

    [Fact]
    public void Enqueue_StoresMessage()
    {
        var buffer = new CriticalMessageBuffer();
        var msg = CreateCompletedMessage("job-1");

        buffer.Enqueue(msg);

        buffer.HasPendingMessages.Should().BeTrue();
        buffer.Count.Should().Be(1);
    }

    [Fact]
    public void DrainAll_ReturnsEnqueuedMessages()
    {
        var buffer = new CriticalMessageBuffer();
        var msg = CreateCompletedMessage("job-1");
        buffer.Enqueue(msg);

        var drained = buffer.DrainAll();

        drained.Should().HaveCount(1);
        drained[0].Should().BeSameAs(msg);
    }

    [Fact]
    public void DrainAll_ClearsBuffer()
    {
        var buffer = new CriticalMessageBuffer();
        buffer.Enqueue(CreateCompletedMessage("job-1"));
        buffer.Enqueue(CreateCompletedMessage("job-2"));

        buffer.DrainAll();

        buffer.HasPendingMessages.Should().BeFalse();
        buffer.Count.Should().Be(0);
    }

    [Fact]
    public void DrainAll_EmptyBuffer_ReturnsEmpty()
    {
        var buffer = new CriticalMessageBuffer();

        var drained = buffer.DrainAll();

        drained.Should().BeEmpty();
        buffer.HasPendingMessages.Should().BeFalse();
    }

    [Fact]
    public void DrainAll_ReturnsInFifoOrder()
    {
        var buffer = new CriticalMessageBuffer();
        var msg1 = CreateCompletedMessage("job-1");
        var msg2 = CreateCompletedMessage("job-2");
        var msg3 = CreateCompletedMessage("job-3");

        buffer.Enqueue(msg1);
        buffer.Enqueue(msg2);
        buffer.Enqueue(msg3);

        var drained = buffer.DrainAll();

        drained.Should().HaveCount(3);
        ((BufferedJobCompleted)drained[0]).JobId.Should().Be("job-1");
        ((BufferedJobCompleted)drained[1]).JobId.Should().Be("job-2");
        ((BufferedJobCompleted)drained[2]).JobId.Should().Be("job-3");
    }

    // ── Overflow Policy ──────────────────────────────────────────────────

    [Fact]
    public void Enqueue_AtCapacity_DropsOldest()
    {
        var buffer = new CriticalMessageBuffer();

        // Fill to capacity (10)
        for (var i = 1; i <= CriticalMessageBuffer.MaxCapacity; i++)
            buffer.Enqueue(CreateCompletedMessage($"job-{i}"));

        buffer.Count.Should().Be(CriticalMessageBuffer.MaxCapacity);

        // Enqueue one more — oldest should be dropped
        buffer.Enqueue(CreateCompletedMessage("job-11"));

        buffer.Count.Should().Be(CriticalMessageBuffer.MaxCapacity);
        var drained = buffer.DrainAll();
        drained.Should().HaveCount(CriticalMessageBuffer.MaxCapacity);

        // First message should be job-2 (job-1 was dropped)
        ((BufferedJobCompleted)drained[0]).JobId.Should().Be("job-2");
        // Last message should be job-11
        ((BufferedJobCompleted)drained[^1]).JobId.Should().Be("job-11");
    }

    [Fact]
    public void Enqueue_MultipleOverflow_DropsMultipleOldest()
    {
        var buffer = new CriticalMessageBuffer();

        // Fill to capacity + 3 extra
        for (var i = 1; i <= CriticalMessageBuffer.MaxCapacity + 3; i++)
            buffer.Enqueue(CreateCompletedMessage($"job-{i}"));

        buffer.Count.Should().Be(CriticalMessageBuffer.MaxCapacity);
        var drained = buffer.DrainAll();

        // Oldest 3 should be dropped
        ((BufferedJobCompleted)drained[0]).JobId.Should().Be("job-4");
        ((BufferedJobCompleted)drained[^1]).JobId.Should().Be("job-13");
    }

    // ── MaxCapacity Constant ─────────────────────────────────────────────

    [Fact]
    public void MaxCapacity_IsTen()
    {
        CriticalMessageBuffer.MaxCapacity.Should().Be(10);
    }

    // ── DrainAttempts Tracking ───────────────────────────────────────────

    [Fact]
    public void DrainAttempts_DefaultIsZero()
    {
        var msg = CreateCompletedMessage("job-1");
        msg.DrainAttempts.Should().Be(0);
    }

    [Fact]
    public void DrainAttempts_IncrementedViaWith()
    {
        var msg = CreateCompletedMessage("job-1");
        var rebuffered = msg with { DrainAttempts = msg.DrainAttempts + 1 };

        rebuffered.DrainAttempts.Should().Be(1);
        rebuffered.JobId.Should().Be("job-1");
    }

    [Fact]
    public void Enqueue_PreservesDrainAttempts()
    {
        var buffer = new CriticalMessageBuffer();
        var msg = new BufferedJobCompleted("job-1", CreatePayload(), DateTimeOffset.UtcNow, DrainAttempts: 2);

        buffer.Enqueue(msg);
        var drained = buffer.DrainAll();

        ((BufferedJobCompleted)drained[0]).DrainAttempts.Should().Be(2);
    }

    // ── Thread Safety ────────────────────────────────────────────────────

    [Fact]
    public async Task ThreadSafety_ConcurrentEnqueue_NoLoss()
    {
        var buffer = new CriticalMessageBuffer();
        const int threadCount = 10;
        const int messagesPerThread = 100;

        var tasks = Enumerable.Range(0, threadCount).Select(threadIdx =>
            Task.Run(() =>
            {
                for (var i = 0; i < messagesPerThread; i++)
                    buffer.Enqueue(CreateCompletedMessage($"thread{threadIdx}-msg{i}"));
            }));

        await Task.WhenAll(tasks);

        // Buffer is bounded to MaxCapacity — only the latest 10 should remain
        buffer.Count.Should().Be(CriticalMessageBuffer.MaxCapacity);
        var drained = buffer.DrainAll();
        drained.Should().HaveCount(CriticalMessageBuffer.MaxCapacity);
    }

    [Fact]
    public async Task ThreadSafety_ConcurrentEnqueueAndDrain_NoException()
    {
        var buffer = new CriticalMessageBuffer();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));

        var enqueueTask = Task.Run(async () =>
        {
            var i = 0;
            while (!cts.Token.IsCancellationRequested)
            {
                buffer.Enqueue(CreateCompletedMessage($"job-{i++}"));
                await Task.Yield();
            }
        });

        var drainTask = Task.Run(async () =>
        {
            while (!cts.Token.IsCancellationRequested)
            {
                buffer.DrainAll();
                await Task.Yield();
            }
        });

        // Should complete without throwing
        await Task.WhenAll(enqueueTask, drainTask);
    }

    // ── HasPendingMessages ───────────────────────────────────────────────

    [Fact]
    public void HasPendingMessages_FalseWhenEmpty()
    {
        var buffer = new CriticalMessageBuffer();
        buffer.HasPendingMessages.Should().BeFalse();
    }

    [Fact]
    public void HasPendingMessages_TrueAfterEnqueue()
    {
        var buffer = new CriticalMessageBuffer();
        buffer.Enqueue(CreateCompletedMessage("job-1"));
        buffer.HasPendingMessages.Should().BeTrue();
    }

    [Fact]
    public void HasPendingMessages_FalseAfterDrain()
    {
        var buffer = new CriticalMessageBuffer();
        buffer.Enqueue(CreateCompletedMessage("job-1"));
        buffer.DrainAll();
        buffer.HasPendingMessages.Should().BeFalse();
    }

    // ── Null Validation ──────────────────────────────────────────────────

    [Fact]
    public void Enqueue_Null_Throws()
    {
        var buffer = new CriticalMessageBuffer();
        var act = () => buffer.Enqueue(null!);
        act.Should().Throw<ArgumentNullException>();
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    private static BufferedJobCompleted CreateCompletedMessage(string jobId) =>
        new(jobId, CreatePayload(), DateTimeOffset.UtcNow);

    private static JobCompletionPayload CreatePayload() => new()
    {
        FinalStep = PipelineStep.Completed,
        CompletedAt = DateTimeOffset.UtcNow
    };
}
