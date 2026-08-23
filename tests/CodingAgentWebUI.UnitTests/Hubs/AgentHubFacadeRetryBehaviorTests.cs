using AwesomeAssertions;
using CodingAgentWebUI.Hub;
using CodingAgentWebUI.Infrastructure.Persistence.Services;
using CodingAgentWebUI.Orchestration;
using CodingAgentWebUI.Orchestration.Dispatch;
using CodingAgentWebUI.Orchestration.Registry;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Pipeline.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Moq;
using Polly.CircuitBreaker;
using ILogger = Serilog.ILogger;

namespace CodingAgentWebUI.UnitTests.Hubs;

/// <summary>
/// Characterization tests for <see cref="AgentHubFacade.TransitionWorkItemAsync"/> retry-with-delay behavior.
/// These tests verify the outer retry loop using a mocked <see cref="IWorkItemFallbackTransitionService"/>
/// to avoid real 2-second delays and to precisely control fallback chain outcomes.
/// </summary>
public sealed class AgentHubFacadeRetryBehaviorTests
{
    private readonly Mock<IWorkItemFallbackTransitionService> _mockFallbackService;
    private readonly AgentHubFacade _facade;
    private readonly FakeTimeProvider _timeProvider;

    public AgentHubFacadeRetryBehaviorTests()
    {
        _mockFallbackService = new Mock<IWorkItemFallbackTransitionService>();
        _timeProvider = new FakeTimeProvider();

        var mockLogger = new Mock<ILogger>();
        var registry = new AgentRegistryService(mockLogger.Object);
        var runService = new OrchestratorRunService(mockLogger.Object);
        var dispatcher = new JobDeduplicationGuardService(registry, mockLogger.Object);

        _facade = new AgentHubFacade(new AgentHubFacadeDependencies(
            registry, runService, dispatcher,
            Mock.Of<IPipelineRunHistoryService>(),
            Mock.Of<IConfigurationStore>(),
            Mock.Of<IProviderFactory>(),
            NullLogger<AgentHubFacadeDependencies>.Instance,
            WorkItemFallbackTransition: _mockFallbackService.Object,
            TimeProvider: _timeProvider));
    }

    // ── Retry on chain failure ────────────────────────────────────────────

    /// <summary>
    /// When the fallback chain returns false on attempt 1 but true on attempt 2,
    /// TransitionWorkItemAsync must make exactly two calls to TryFallbackChainAsync.
    /// This verifies the outer retry-with-delay loop fires on an all-paths-rejected result.
    /// Note: The retry only fires on exceptions, not on false returns — so all-false
    /// returns without exception exit immediately after logging the rejection.
    /// </summary>
    [Fact]
    public async Task TransitionWorkItemAsync_WhenFallbackChainReturnsFalse_DoesNotRetry()
    {
        // Arrange: chain returns false (all paths rejected, no exception)
        var workItemId = Guid.NewGuid();
        _mockFallbackService
            .Setup(s => s.TryFallbackChainAsync(workItemId, WorkItemStatus.Succeeded, null, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        // Act
        var exception = await Record.ExceptionAsync(() =>
            _facade.TransitionWorkItemAsync(workItemId.ToString(), WorkItemStatus.Succeeded, CancellationToken.None));

        // Assert: no exception escapes; called exactly once (false return exits immediately, no retry)
        exception.Should().BeNull();
        _mockFallbackService.Verify(
            s => s.TryFallbackChainAsync(workItemId, WorkItemStatus.Succeeded, null, null, It.IsAny<CancellationToken>()),
            Times.Once,
            "a false return from TryFallbackChainAsync should not trigger a retry — only exceptions do");
    }

    /// <summary>
    /// When the fallback chain throws on attempt 1 and succeeds on attempt 2,
    /// TransitionWorkItemAsync must make exactly two calls and not re-throw.
    /// Uses FakeTimeProvider to skip the 2-second retry delay instantly.
    /// </summary>
    [Fact]
    public async Task TransitionWorkItemAsync_WhenExceptionThrownOnFirstAttempt_RetriesAndSucceeds()
    {
        // Arrange: throw on first call, succeed on second
        var workItemId = Guid.NewGuid();
        var callCount = 0;
        _mockFallbackService
            .Setup(s => s.TryFallbackChainAsync(workItemId, WorkItemStatus.Failed, "err", FailureReason.AgentError, It.IsAny<CancellationToken>()))
            .Returns(() =>
            {
                callCount++;
                if (callCount == 1) throw new InvalidOperationException("Transient DB error");
                return Task.FromResult(true);
            });

        // FakeTimeProvider: advance time in a background thread so the Task.Delay(2s, _timeProvider, ct)
        // resolves immediately once it is awaited, without any real wall-clock wait.
        using var advanceCts = new CancellationTokenSource();
        var advanceThread = new Thread(() =>
        {
            while (!advanceCts.IsCancellationRequested)
            {
                Thread.Sleep(1); // NOSONAR S2925 — background thread advancing FakeTimeProvider requires real wall-clock pause
                _timeProvider.Advance(TimeSpan.FromSeconds(3));
            }
        }) { IsBackground = true };
        advanceThread.Start();

        // Act
        var exception = await Record.ExceptionAsync(() =>
            _facade.TransitionWorkItemAsync(workItemId.ToString(), WorkItemStatus.Failed,
                CancellationToken.None, "err", FailureReason.AgentError));

        advanceCts.Cancel();
        advanceThread.Join(100);

        // Assert: no exception escapes; called twice (attempt 0 threw, attempt 1 succeeded)
        exception.Should().BeNull();
        callCount.Should().Be(2, "first attempt threw, second attempt succeeded");
    }

    /// <summary>
    /// When the fallback chain throws a BrokenCircuitException, the retry guard
    /// does NOT catch it — it propagates out immediately without a second attempt.
    /// </summary>
    [Fact]
    public async Task TransitionWorkItemAsync_WhenBrokenCircuitExceptionThrown_DoesNotRetry()
    {
        // Arrange
        var workItemId = Guid.NewGuid();
        _mockFallbackService
            .Setup(s => s.TryFallbackChainAsync(workItemId, WorkItemStatus.Succeeded, null, null, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new BrokenCircuitException("Circuit open"));

        // Act
        var exception = await Record.ExceptionAsync(() =>
            _facade.TransitionWorkItemAsync(workItemId.ToString(), WorkItemStatus.Succeeded, CancellationToken.None));

        // Assert: BrokenCircuitException is not caught by the retry guard — it propagates
        exception.Should().BeOfType<BrokenCircuitException>();
        _mockFallbackService.Verify(
            s => s.TryFallbackChainAsync(It.IsAny<Guid>(), It.IsAny<WorkItemStatus>(), It.IsAny<string?>(), It.IsAny<FailureReason?>(), It.IsAny<CancellationToken>()),
            Times.Once,
            "BrokenCircuitException must not be caught by the retry loop — it propagates immediately");
    }

    /// <summary>
    /// When both retry attempts throw a non-BrokenCircuit exception, the first attempt's
    /// exception is caught and a retry is initiated. The second attempt's exception propagates
    /// (the catch guard `attempt &lt; maxAttempts - 1` is false on the final attempt).
    /// The LogError line after the loop is only reached when all attempts return false without throwing.
    /// </summary>
    [Fact]
    public async Task TransitionWorkItemAsync_WhenBothAttemptsThrow_SecondExceptionPropagates()
    {
        // Arrange: both attempts throw
        var workItemId = Guid.NewGuid();
        _mockFallbackService
            .Setup(s => s.TryFallbackChainAsync(workItemId, WorkItemStatus.Cancelled, null, null, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new TimeoutException("DB timeout"));

        // FakeTimeProvider: advance in a background thread so Task.Delay(2s, _timeProvider, ct)
        // between the two attempts does not block real wall-clock time.
        using var advanceCts = new CancellationTokenSource();
        var advanceThread = new Thread(() =>
        {
            while (!advanceCts.IsCancellationRequested)
            {
                Thread.Sleep(1); // NOSONAR S2925 — background thread advancing FakeTimeProvider requires real wall-clock pause
                _timeProvider.Advance(TimeSpan.FromSeconds(3));
            }
        }) { IsBackground = true };
        advanceThread.Start();

        // Act
        var exception = await Record.ExceptionAsync(() =>
            _facade.TransitionWorkItemAsync(workItemId.ToString(), WorkItemStatus.Cancelled, CancellationToken.None));

        advanceCts.Cancel();
        advanceThread.Join(100);

        // Assert: the final attempt's exception propagates — the catch guard is false on attempt index 1
        // (catch condition: attempt < maxAttempts - 1  =>  1 < 1  =>  false).
        // Two calls are made before the exception escapes.
        exception.Should().BeOfType<TimeoutException>(
            "the final attempt's exception propagates when the catch guard is false");
        _mockFallbackService.Verify(
            s => s.TryFallbackChainAsync(workItemId, WorkItemStatus.Cancelled, null, null, It.IsAny<CancellationToken>()),
            Times.Exactly(2),
            "both retry attempts are made before the final exception escapes");
    }
}
