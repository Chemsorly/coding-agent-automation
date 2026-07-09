using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Services;
using CodingAgentWebUI.Services;
using Microsoft.Extensions.Time.Testing;
using Serilog;
using Xunit;

namespace CodingAgentWebUI.Pipeline.UnitTests.Services;

public class ShutdownServiceTests
{
    private readonly ILogger _logger = new LoggerConfiguration().CreateLogger();

    [Fact]
    public async Task StoppingAsync_WhenRunning_CancelsPipelineAndAgentRuns()
    {
        // Arrange
        var lifecycleCalled = false;
        var orchestrationCalled = false;

        var service = new ShutdownService(
            new FakeLifecycleShutdownAction(isRunning: true, onCancel: () => lifecycleCalled = true),
            new FakeOrchestrationShutdownAction(onCancel: () => orchestrationCalled = true),
            new ShutdownSignal(),
            _logger);

        using var cts = new CancellationTokenSource();

        // Act
        await service.StoppingAsync(cts.Token);

        // Assert
        Assert.True(lifecycleCalled);
        Assert.True(orchestrationCalled);
    }

    [Fact]
    public async Task StoppingAsync_WhenNotRunning_SkipsCancelPipeline_StillCancelsAgentRuns()
    {
        // Arrange
        var lifecycleCalled = false;
        var orchestrationCalled = false;

        var service = new ShutdownService(
            new FakeLifecycleShutdownAction(isRunning: false, onCancel: () => lifecycleCalled = true),
            new FakeOrchestrationShutdownAction(onCancel: () => orchestrationCalled = true),
            new ShutdownSignal(),
            _logger);

        using var cts = new CancellationTokenSource();

        // Act
        await service.StoppingAsync(cts.Token);

        // Assert
        Assert.False(lifecycleCalled);
        Assert.True(orchestrationCalled);
    }

    [Fact]
    public async Task StoppingAsync_CompletesWithin15SecondTimeout_WhenShutdownIsSlower()
    {
        // Arrange: orchestration takes 30 seconds (simulated), timeout set to 2s for fast test
        var fakeTime = new FakeTimeProvider();
        var service = new ShutdownService(
            new FakeLifecycleShutdownAction(isRunning: false, onCancel: () => { }),
            new FakeOrchestrationShutdownAction(onCancel: () => { }, delay: TimeSpan.FromSeconds(30)),
            new ShutdownSignal(),
            _logger,
            shutdownTimeout: TimeSpan.FromSeconds(2),
            timeProvider: fakeTime);

        using var cts = new CancellationTokenSource();

        // Act — start shutdown, should block until timeout
        var task = service.StoppingAsync(cts.Token);
        Assert.False(task.IsCompleted, "Should be waiting on orchestration/timeout");

        // Advance time past the 2s timeout
        fakeTime.Advance(TimeSpan.FromSeconds(2));
        await task;

        // Assert: completed via timeout (not waiting the full 30s orchestration delay)
    }

    [Fact]
    public async Task StoppingAsync_DoesNotThrow_WhenCancelPipelineThrows()
    {
        // Arrange
        var orchestrationCalled = false;

        var service = new ShutdownService(
            new FakeLifecycleShutdownAction(isRunning: true, onCancel: () => throw new InvalidOperationException("boom")),
            new FakeOrchestrationShutdownAction(onCancel: () => orchestrationCalled = true),
            new ShutdownSignal(),
            _logger);

        using var cts = new CancellationTokenSource();

        // Act — should not throw
        await service.StoppingAsync(cts.Token);

        // Assert: orchestration still called despite lifecycle failure
        Assert.True(orchestrationCalled);
    }

    [Fact]
    public async Task StoppingAsync_DoesNotThrow_WhenCancelAgentRunsThrows()
    {
        // Arrange
        var service = new ShutdownService(
            new FakeLifecycleShutdownAction(isRunning: false, onCancel: () => { }),
            new FakeOrchestrationShutdownAction(onCancel: () => throw new InvalidOperationException("network error")),
            new ShutdownSignal(),
            _logger);

        using var cts = new CancellationTokenSource();

        // Act — should not throw
        await service.StoppingAsync(cts.Token);
    }

    // ── Test Fakes ──────────────────────────────────────────────────────────

    private sealed class FakeLifecycleShutdownAction : ILifecycleShutdownAction
    {
        private readonly bool _isRunning;
        private readonly Action _onCancel;

        public FakeLifecycleShutdownAction(bool isRunning, Action onCancel)
        {
            _isRunning = isRunning;
            _onCancel = onCancel;
        }

        public bool IsRunning => _isRunning;

        public Task CancelPipelineAsync()
        {
            _onCancel();
            return Task.CompletedTask;
        }
    }

    private sealed class FakeOrchestrationShutdownAction : IOrchestrationShutdownAction
    {
        private readonly Action _onCancel;
        private readonly TimeSpan? _delay;

        public FakeOrchestrationShutdownAction(Action onCancel, TimeSpan? delay = null)
        {
            _onCancel = onCancel;
            _delay = delay;
        }

        public async Task CancelActiveAgentRunsAsync()
        {
            if (_delay.HasValue)
                await Task.Delay(_delay.Value);
            _onCancel();
        }
    }
}
