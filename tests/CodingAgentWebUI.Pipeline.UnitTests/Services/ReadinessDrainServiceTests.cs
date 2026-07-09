using CodingAgentWebUI.Services;
using Microsoft.Extensions.Time.Testing;
using Serilog;
using Xunit;

namespace CodingAgentWebUI.Pipeline.UnitTests.Services;

public class ReadinessDrainServiceTests
{
    private readonly ILogger _logger = new LoggerConfiguration().CreateLogger();

    [Fact]
    public void ReadinessState_IsReady_TrueByDefault()
    {
        var state = new ReadinessState();
        Assert.True(state.IsReady);
    }

    [Fact]
    public void ReadinessState_MarkNotReady_FlipsToFalse()
    {
        var state = new ReadinessState();
        state.MarkNotReady();
        Assert.False(state.IsReady);
    }

    [Fact]
    public async Task StoppingAsync_FlipsReadinessState()
    {
        var state = new ReadinessState();
        var service = new ReadinessDrainService(state, _logger, drainDelay: TimeSpan.Zero);

        using var cts = new CancellationTokenSource();
        await service.StoppingAsync(cts.Token);

        Assert.False(state.IsReady);
    }

    [Fact]
    public async Task StoppingAsync_WaitsForDrainDelay()
    {
        var state = new ReadinessState();
        var delay = TimeSpan.FromMilliseconds(200);
        var fakeTime = new FakeTimeProvider();
        var service = new ReadinessDrainService(state, _logger, drainDelay: delay, timeProvider: fakeTime);

        using var cts = new CancellationTokenSource();
        var task = service.StoppingAsync(cts.Token);

        // Task should be waiting on the delay — not yet completed
        Assert.False(task.IsCompleted, "Task should be waiting on drain delay");

        // Advance past the drain delay
        fakeTime.Advance(delay);
        await task;

        // State should have been flipped
        Assert.False(state.IsReady);
    }

    [Fact]
    public async Task StoppingAsync_CancellationAbortsDelay()
    {
        var state = new ReadinessState();
        var delay = TimeSpan.FromSeconds(30); // long delay
        var fakeTime = new FakeTimeProvider();
        var service = new ReadinessDrainService(state, _logger, drainDelay: delay, timeProvider: fakeTime);

        using var cts = new CancellationTokenSource();
        var task = service.StoppingAsync(cts.Token);

        // Task should be waiting on the 30s delay
        Assert.False(task.IsCompleted, "Task should be waiting on drain delay");

        // Cancel instead of advancing time
        cts.Cancel();
        await task;

        // State still flipped before cancellation
        Assert.False(state.IsReady);
    }

    [Fact]
    public void ResolveDrainDelay_DefaultIs15Seconds()
    {
        // Clear env var to test default
        Environment.SetEnvironmentVariable("READINESS_DRAIN_DELAY_SECONDS", null);
        try
        {
            var delay = ReadinessDrainService.ResolveDrainDelay();
            Assert.Equal(TimeSpan.FromSeconds(15), delay);
        }
        finally
        {
            Environment.SetEnvironmentVariable("READINESS_DRAIN_DELAY_SECONDS", null);
        }
    }

    [Theory]
    [InlineData("0", 0)]
    [InlineData("5", 5)]
    [InlineData("30", 30)]
    [InlineData("120", 120)]
    public void ResolveDrainDelay_ParsesValidValues(string envValue, int expectedSeconds)
    {
        Environment.SetEnvironmentVariable("READINESS_DRAIN_DELAY_SECONDS", envValue);
        try
        {
            var delay = ReadinessDrainService.ResolveDrainDelay();
            Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), delay);
        }
        finally
        {
            Environment.SetEnvironmentVariable("READINESS_DRAIN_DELAY_SECONDS", null);
        }
    }

    [Theory]
    [InlineData("-10", 0)]   // clamped to 0
    [InlineData("999", 120)] // clamped to 120
    public void ResolveDrainDelay_ClampsOutOfBoundsValues(string envValue, int expectedSeconds)
    {
        Environment.SetEnvironmentVariable("READINESS_DRAIN_DELAY_SECONDS", envValue);
        try
        {
            var delay = ReadinessDrainService.ResolveDrainDelay();
            Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), delay);
        }
        finally
        {
            Environment.SetEnvironmentVariable("READINESS_DRAIN_DELAY_SECONDS", null);
        }
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("")]
    [InlineData("12.5")]
    public void ResolveDrainDelay_InvalidStringFallsBackToDefault(string envValue)
    {
        Environment.SetEnvironmentVariable("READINESS_DRAIN_DELAY_SECONDS", envValue);
        try
        {
            var delay = ReadinessDrainService.ResolveDrainDelay();
            Assert.Equal(TimeSpan.FromSeconds(15), delay);
        }
        finally
        {
            Environment.SetEnvironmentVariable("READINESS_DRAIN_DELAY_SECONDS", null);
        }
    }

    [Fact]
    public async Task StoppingAsync_FlipsState_BeforeDelayBegins()
    {
        // Verify state is flipped immediately, not after delay
        var state = new ReadinessState();
        var delay = TimeSpan.FromSeconds(10);
        var service = new ReadinessDrainService(state, _logger, drainDelay: delay);

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        await service.StoppingAsync(cts.Token);

        // Even though delay was cancelled, state was already flipped
        Assert.False(state.IsReady);
    }
}
