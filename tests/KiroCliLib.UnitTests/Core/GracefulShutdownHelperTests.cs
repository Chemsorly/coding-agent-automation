using KiroCliLib.Core;
using Moq;
using ILogger = Serilog.ILogger;

namespace KiroCliLib.UnitTests.Core;

/// <summary>
/// Unit tests for GracefulShutdownHelper.
/// Validates: null task returns immediately, null CTS still awaits task,
/// task completes before timeout (no warning), task exceeds timeout (logs warning).
/// Requirements: 37.2–37.3
/// </summary>
public class GracefulShutdownHelperTests
{
    private readonly Mock<ILogger> _mockLogger = new();

    [Fact]
    public async Task CancelAndWaitAsync_NullTask_ReturnsImmediately()
    {
        // Null task should return immediately without throwing
        using var cts = new CancellationTokenSource();

        await GracefulShutdownHelper.CancelAndWaitAsync(
            cts, task: null, TimeSpan.FromSeconds(5), _mockLogger.Object, "TestOp");

        // No exception thrown, no warning logged
        _mockLogger.Verify(
            l => l.Warning(It.IsAny<string>(), It.IsAny<object[]>()),
            Times.Never);
    }

    [Fact]
    public async Task CancelAndWaitAsync_NullCts_StillAwaitsTask()
    {
        // Null CTS should not throw; the task should still be awaited
        var tcs = new TaskCompletionSource();
        tcs.SetResult(); // Complete immediately

        await GracefulShutdownHelper.CancelAndWaitAsync(
            cts: null, task: tcs.Task, TimeSpan.FromSeconds(5), _mockLogger.Object, "TestOp");

        // No exception thrown — null CTS is handled gracefully
    }

    [Fact]
    public async Task CancelAndWaitAsync_TaskCompletesBeforeTimeout_NoWarningLogged()
    {
        // Task that completes quickly should not trigger a timeout warning
        using var cts = new CancellationTokenSource();
        var task = Task.CompletedTask;

        await GracefulShutdownHelper.CancelAndWaitAsync(
            cts, task, TimeSpan.FromSeconds(5), _mockLogger.Object, "FastOp");

        // Verify no warning was logged
        _mockLogger.Verify(
            l => l.Warning(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<TimeSpan>()),
            Times.Never);
    }

    [Fact]
    public async Task CancelAndWaitAsync_TaskExceedsTimeout_LogsWarningWithOperationName()
    {
        // Task that never completes should trigger a timeout warning
        using var cts = new CancellationTokenSource();
        var tcs = new TaskCompletionSource(); // Never completes

        await GracefulShutdownHelper.CancelAndWaitAsync(
            cts, tcs.Task, TimeSpan.FromMilliseconds(50), _mockLogger.Object, "SlowOperation");

        // Verify warning was logged with the operation name
        _mockLogger.Verify(
            l => l.Warning(It.IsAny<string>(), "SlowOperation", It.IsAny<TimeSpan>()),
            Times.Once);
    }

    [Fact]
    public async Task CancelAndWaitAsync_TaskRespondsToCancellation_NoWarningLogged()
    {
        // Task that responds to cancellation should not trigger a warning
        using var cts = new CancellationTokenSource();
        var task = Task.Delay(Timeout.Infinite, cts.Token);

        await GracefulShutdownHelper.CancelAndWaitAsync(
            cts, task, TimeSpan.FromSeconds(5), _mockLogger.Object, "CancellableOp");

        // OperationCanceledException is caught internally — no warning
        _mockLogger.Verify(
            l => l.Warning(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<TimeSpan>()),
            Times.Never);
    }
}
