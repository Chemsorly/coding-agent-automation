using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Services;
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
        var service = new ShutdownService(
            new FakeLifecycleShutdownAction(isRunning: false, onCancel: () => { }),
            new FakeOrchestrationShutdownAction(onCancel: () => { }, delay: TimeSpan.FromSeconds(30)),
            _logger,
            shutdownTimeout: TimeSpan.FromSeconds(2));

        using var cts = new CancellationTokenSource();

        // Act — should complete due to 2s timeout, not wait 30s
        var sw = System.Diagnostics.Stopwatch.StartNew();
        await service.StoppingAsync(cts.Token);
        sw.Stop();

        // Assert: completed well under 30s (timeout kicked in at 2s)
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task StoppingAsync_DoesNotThrow_WhenCancelPipelineThrows()
    {
        // Arrange
        var orchestrationCalled = false;

        var service = new ShutdownService(
            new FakeLifecycleShutdownAction(isRunning: true, onCancel: () => throw new InvalidOperationException("boom")),
            new FakeOrchestrationShutdownAction(onCancel: () => orchestrationCalled = true),
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
