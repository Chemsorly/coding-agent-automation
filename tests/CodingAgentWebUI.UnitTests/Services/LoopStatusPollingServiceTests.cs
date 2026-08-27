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

        // Wait for the catch block in ExecuteAsync to finish writing _isSchedulerUnreachable.
        // The TCS fires inside the mock lambda before the throw propagates, so the catch has
        // not yet run by the time secondCallCompleted is set. Poll the property instead of
        // using a fixed Task.Delay so the test is deterministic on any machine speed.
        var unreachableSet = await Task.WhenAny(
            Task.Run(async () => { while (!svc.IsSchedulerUnreachable) await Task.Yield(); }),
            Task.Delay(TimeSpan.FromSeconds(5)));
        unreachableSet.IsCompletedSuccessfully.Should().BeTrue("IsSchedulerUnreachable must be set within 5s");

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
        var secondCallCompleted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _mockClient.Setup(c => c.GetLoopStatusAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            {
                callCount++;
                if (callCount == 1) throw new HttpRequestException("first call fails");
                var result = DefaultStatus;
                secondCallCompleted.TrySetResult(); // signal that the second poll succeeded
                return result;
            });

        var svc = CreateService(interval: TimeSpan.FromMilliseconds(1));

        // Act: wait until the second poll has actually completed, then stop
        await svc.StartAsync(CancellationToken.None);
        await secondCallCompleted.Task.WaitAsync(TimeSpan.FromSeconds(5)); // definitive signal, not a fixed delay
        using var stopCts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        try { await svc.StopAsync(stopCts.Token); } catch { }
        svc.Dispose();

        // Assert: unreachable cleared after recovery
        svc.IsSchedulerUnreachable.Should().BeFalse("unreachable flag must be cleared on recovery");
        svc.IsLoopActive.Should().Be(DefaultStatus.IsLoopActive);
    }

    [Fact]
    public async Task WhenOnChangeSubscriberThrows_OtherSubscribersStillFire()
    {
        _mockClient.Setup(c => c.GetLoopStatusAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(DefaultStatus);

        var svc = CreateService();
        var secondFired = false;
        svc.OnChange += () => throw new InvalidOperationException("bad subscriber");
        svc.OnChange += () => { secondFired = true; };

        await RunServiceForDurationAsync(svc, TimeSpan.FromMilliseconds(50));

        // The throwing subscriber must not prevent the second subscriber from firing
        secondFired.Should().BeTrue("subscriber exception must be caught per-subscriber, not abort the loop");
    }
}
