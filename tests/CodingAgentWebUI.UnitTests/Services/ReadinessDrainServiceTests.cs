using AwesomeAssertions;
using CodingAgentWebUI.Services;
using Microsoft.Extensions.Time.Testing;
using Moq;

namespace CodingAgentWebUI.UnitTests.Services;

/// <summary>
/// Unit tests for <see cref="ReadinessDrainService"/>.
/// Validates: drain sequence, cancellation handling, and drain delay clamping.
/// </summary>
public class ReadinessDrainServiceTests
{
    // ── StoppingAsync: marks not-ready before delay elapses ──────────────────

    [Fact]
    public async Task StoppingAsync_MarksNotReady_BeforeDelayElapses()
    {
        var readiness = new ReadinessState();
        var fakeTime = new FakeTimeProvider();
        var svc = new ReadinessDrainService(readiness, Serilog.Log.Logger,
            drainDelay: TimeSpan.FromSeconds(10), timeProvider: fakeTime);

        // Start StoppingAsync but don't advance time — it should block on Task.Delay
        var stoppingTask = svc.StoppingAsync(CancellationToken.None);

        // MarkNotReady is synchronous and happens before the await, so by the time
        // we reach here after scheduling the task, readiness is already marked.
        await Task.Yield(); // let the task start

        readiness.IsReady.Should().BeFalse(
            "MarkNotReady must be called before the drain delay starts");

        // Advance time past the drain to let the task complete
        fakeTime.Advance(TimeSpan.FromSeconds(11));
        await stoppingTask.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task StoppingAsync_CompletesAfterDelayElapses()
    {
        var readiness = new ReadinessState();
        var fakeTime = new FakeTimeProvider();
        var svc = new ReadinessDrainService(readiness, Serilog.Log.Logger,
            drainDelay: TimeSpan.FromSeconds(15), timeProvider: fakeTime);

        var stoppingTask = svc.StoppingAsync(CancellationToken.None);

        // Task should not be complete before the delay elapses
        await Task.Yield();
        stoppingTask.IsCompleted.Should().BeFalse(
            "StoppingAsync should be waiting for the drain delay");

        // Advance past the delay
        fakeTime.Advance(TimeSpan.FromSeconds(16));
        await stoppingTask.WaitAsync(TimeSpan.FromSeconds(5));

        stoppingTask.IsCompletedSuccessfully.Should().BeTrue(
            "StoppingAsync must complete after the drain delay elapses");
    }

    // ── StoppingAsync: cancellation is swallowed (does not rethrow) ──────────

    [Fact]
    public async Task StoppingAsync_Cancellation_LogsWarningAndCompletes_WithoutThrowing()
    {
        var readiness = new ReadinessState();
        var fakeTime = new FakeTimeProvider();
        using var cts = new CancellationTokenSource();

        var svc = new ReadinessDrainService(readiness, Serilog.Log.Logger,
            drainDelay: TimeSpan.FromSeconds(60), timeProvider: fakeTime);

        var stoppingTask = svc.StoppingAsync(cts.Token);

        await Task.Yield();
        cts.Cancel();

        // OperationCanceledException must be caught internally — the task completes, not faults
        var ex = await Record.ExceptionAsync(() => stoppingTask.WaitAsync(TimeSpan.FromSeconds(5)));
        ex.Should().BeNull("StoppingAsync must not propagate OperationCanceledException on cancellation");
        stoppingTask.IsCompletedSuccessfully.Should().BeTrue(
            "cancelled drain must still complete normally (not fault)");
    }

    // ── ResolveDrainDelay: default when env var absent ───────────────────────

    [Fact]
    public void ResolveDrainDelay_NoEnvVar_Returns15Seconds()
    {
        Environment.SetEnvironmentVariable("READINESS_DRAIN_DELAY_SECONDS", null);
        try
        {
            ReadinessDrainService.ResolveDrainDelay()
                .Should().Be(TimeSpan.FromSeconds(15),
                    "default drain delay must be 15 s when env var is unset");
        }
        finally
        {
            Environment.SetEnvironmentVariable("READINESS_DRAIN_DELAY_SECONDS", null);
        }
    }

    // ── ResolveDrainDelay: clamping ───────────────────────────────────────────

    [Theory]
    [InlineData("-5", 0)]       // below minimum → 0
    [InlineData("0", 0)]        // exactly 0
    [InlineData("30", 30)]      // mid-range value passes through
    [InlineData("120", 120)]    // exactly at maximum
    [InlineData("200", 120)]    // above maximum → clamped to 120
    [InlineData("9999", 120)]   // far above maximum → clamped to 120
    public void ResolveDrainDelay_ValidInteger_ClampsToRange(string envValue, int expectedSeconds)
    {
        Environment.SetEnvironmentVariable("READINESS_DRAIN_DELAY_SECONDS", envValue);
        try
        {
            ReadinessDrainService.ResolveDrainDelay()
                .Should().Be(TimeSpan.FromSeconds(expectedSeconds),
                    $"env var '{envValue}' must resolve to {expectedSeconds} s");
        }
        finally
        {
            Environment.SetEnvironmentVariable("READINESS_DRAIN_DELAY_SECONDS", null);
        }
    }

    // ── ResolveDrainDelay: non-parseable falls back to default ────────────────

    [Theory]
    [InlineData("abc")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("1.5")]         // float not accepted (int.TryParse)
    [InlineData("not-a-number")]
    public void ResolveDrainDelay_UnparseableValue_ReturnsDefault(string envValue)
    {
        // Empty/whitespace: null is the correct way to "unset"; non-empty whitespace hits the parse path
        Environment.SetEnvironmentVariable("READINESS_DRAIN_DELAY_SECONDS", envValue == "" ? null : envValue);
        try
        {
            ReadinessDrainService.ResolveDrainDelay()
                .Should().Be(TimeSpan.FromSeconds(15),
                    $"unparseable env var '{envValue}' must fall back to 15 s default");
        }
        finally
        {
            Environment.SetEnvironmentVariable("READINESS_DRAIN_DELAY_SECONDS", null);
        }
    }
}
