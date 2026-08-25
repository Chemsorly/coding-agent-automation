using AwesomeAssertions;
using CodingAgentWebUI.Api.Client;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Services;
using Moq;
using Xunit;
using ILogger = Serilog.ILogger;

namespace CodingAgentWebUI.UnitTests.Services;

/// <summary>
/// Tests for <see cref="LoopStatusPollingService"/>.
/// TDD: written before the implementation.
/// </summary>
public sealed class LoopStatusPollingServiceTests
{
    private readonly Mock<ISchedulerApiClient> _mockClient;
    private readonly Mock<ILogger> _mockLogger;

    private static readonly LoopStatusDto DefaultStatus = new(
        true, "Running", "issue-1", 5, 2, 3, false, null, 1, 2,
        new[] { "Error A" },
        new Dictionary<string, ConfigStatusSnapshot> { ["t1"] = ConfigStatusSnapshot.Empty });

    public LoopStatusPollingServiceTests()
    {
        _mockClient = new Mock<ISchedulerApiClient>();
        _mockLogger = new Mock<ILogger>();
        _mockLogger.Setup(l => l.ForContext<LoopStatusPollingService>()).Returns(_mockLogger.Object);
        _mockLogger.Setup(l => l.ForContext(It.IsAny<string>(), It.IsAny<object>(), It.IsAny<bool>()))
            .Returns(_mockLogger.Object);
    }

    private LoopStatusPollingService CreateService(TimeSpan? interval = null)
        => new LoopStatusPollingService(
            _mockClient.Object,
            _mockLogger.Object,
            interval ?? TimeSpan.FromMilliseconds(1));

    private static async Task RunServiceForDurationAsync(LoopStatusPollingService svc, TimeSpan duration)
    {
        using var startCts = new CancellationTokenSource();
        await svc.StartAsync(startCts.Token);
        await Task.Delay(duration, CancellationToken.None);
        using var stopCts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        try { await svc.StopAsync(stopCts.Token); } catch { }
        svc.Dispose();
    }

    [Fact]
    public async Task WhenPollSucceeds_PropertiesUpdatedAndOnChangeFiredAndUnreachableFalse()
    {
        // Arrange
        _mockClient.Setup(c => c.GetLoopStatusAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(DefaultStatus);

        var svc = CreateService();
        var onChangeFired = false;
        svc.OnChange += () => onChangeFired = true;

        // Act
        await RunServiceForDurationAsync(svc, TimeSpan.FromMilliseconds(50));

        // Assert: properties match the DTO
        svc.IsLoopActive.Should().Be(DefaultStatus.IsLoopActive);
        svc.StatusMessage.Should().Be(DefaultStatus.StatusMessage);
        svc.ProcessedCount.Should().Be(DefaultStatus.ProcessedCount);
        svc.FailedCount.Should().Be(DefaultStatus.FailedCount);
        svc.ValidationErrors.Should().BeEquivalentTo(DefaultStatus.ValidationErrors);
        svc.IsSchedulerUnreachable.Should().BeFalse("successful poll clears unreachable flag");
        onChangeFired.Should().BeTrue("OnChange should fire after successful poll");
    }

    [Fact]
    public async Task WhenPollFails_IsSchedulerUnreachableTrueAndPriorStatePreserved()
    {
        // Arrange: first call succeeds, second fails
        var callCount = 0;
        var secondCallCompleted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _mockClient.Setup(c => c.GetLoopStatusAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            {
                callCount++;
                if (callCount == 1) return DefaultStatus;
                var ex = new HttpRequestException("connection refused");
                secondCallCompleted.TrySetResult();
                throw ex;
            });

        var svc = CreateService(interval: TimeSpan.FromMilliseconds(1));
        await svc.StartAsync(CancellationToken.None);

        // Act: wait until the second call has completed (deterministic, not wall-clock)
        var completed = await Task.WhenAny(secondCallCompleted.Task, Task.Delay(TimeSpan.FromSeconds(5)));
        completed.Should().BeSameAs(secondCallCompleted.Task, "second poll call should complete within 5s");

        // Small yield so the catch block in ExecuteAsync can finish setting _isSchedulerUnreachable
        await Task.Delay(20);

        // Assert: unreachable set after failure; prior state preserved (not reset to defaults)
        svc.IsSchedulerUnreachable.Should().BeTrue("poll failure must set IsSchedulerUnreachable");
        // Status from the first successful call should be preserved, not reset to empty
        svc.StatusMessage.Should().Be(DefaultStatus.StatusMessage,
            "prior state must be preserved when poll fails");

        using var stopCts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        try { await svc.StopAsync(stopCts.Token); } catch { }
        svc.Dispose();
    }

    [Fact]
    public async Task WhenPollRecovery_IsSchedulerUnreachableCleared()
    {
        // Arrange: first call fails, second succeeds
        var callCount = 0;
        _mockClient.Setup(c => c.GetLoopStatusAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            {
                callCount++;
                if (callCount == 1) throw new HttpRequestException("first call fails");
                return DefaultStatus;
            });

        var svc = CreateService(interval: TimeSpan.FromMilliseconds(1));

        // Act: run long enough for at least 2 ticks
        await RunServiceForDurationAsync(svc, TimeSpan.FromMilliseconds(100));

        // Assert: unreachable cleared after recovery
        svc.IsSchedulerUnreachable.Should().BeFalse("unreachable flag must be cleared on recovery");
        svc.IsLoopActive.Should().Be(DefaultStatus.IsLoopActive);
    }
}
