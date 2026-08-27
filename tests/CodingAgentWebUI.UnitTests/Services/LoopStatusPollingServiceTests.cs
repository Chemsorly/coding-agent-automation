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
        // TODO: This fixed 20 ms delay is a non-deterministic synchronization barrier. On a heavily
        // loaded CI runner the OS scheduler can preempt the service task for longer than 20 ms between
        // the TCS signal and the flag write, causing the assertion below to race. The original polling
        // loop (while (!svc.IsSchedulerUnreachable) await Task.Yield()) was deterministic. Consider
        // restoring a polling approach with a hard 5 s timeout to eliminate this potential flakiness.
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
        var secondCallCompleted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _mockClient.Setup(c => c.GetLoopStatusAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            {
                callCount++;
                if (callCount == 1) throw new HttpRequestException("first call fails");
                secondCallCompleted.TrySetResult();
                return DefaultStatus;
            });

        var svc = CreateService(interval: TimeSpan.FromMilliseconds(1));
        await svc.StartAsync(CancellationToken.None);

        // Act: wait until the second (successful) call has completed — deterministic, not wall-clock
        var completed = await Task.WhenAny(secondCallCompleted.Task, Task.Delay(TimeSpan.FromSeconds(5)));
        completed.Should().BeSameAs(secondCallCompleted.Task, "second poll call should complete within 5s");

        // Small yield so the service loop can finish updating _isSchedulerUnreachable and _status
        // TODO: Same non-deterministic 20 ms delay as in WhenPollFails_IsSchedulerUnreachableSet. On a
        // busy CI runner this window may be insufficient for the continuation updating _isSchedulerUnreachable
        // to complete after the second successful poll fires the TCS. Consider a deterministic polling
        // approach with a hard timeout to avoid intermittent false failures on this assertion.
        await Task.Delay(20);

        // Assert: unreachable cleared after recovery; status updated from successful poll
        svc.IsSchedulerUnreachable.Should().BeFalse("unreachable flag must be cleared on recovery");
        svc.IsLoopActive.Should().Be(DefaultStatus.IsLoopActive);

        using var stopCts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        try { await svc.StopAsync(stopCts.Token); } catch { }
        svc.Dispose();
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
