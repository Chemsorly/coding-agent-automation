using AwesomeAssertions;
using CodingAgentWebUI.Api.Dispatch;
using DispatchLifecycleService = CodingAgentWebUI.Api.Dispatch.DispatchLifecycleService;
using DispatchStateBuilder = CodingAgentWebUI.Api.Dispatch.DispatchStateBuilder;
using DispatchTemplateResolver = CodingAgentWebUI.Api.Dispatch.DispatchTemplateResolver;
using CodingAgentWebUI.Infrastructure.Persistence;
using CodingAgentWebUI.Infrastructure.Persistence.Services;
using CodingAgentWebUI.Orchestration.Dispatch;
using CodingAgentWebUI.Pipeline.LeaderElection;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using System.Reflection;

namespace CodingAgentWebUI.Orchestration.UnitTests.Dispatch;

/// <summary>
/// Unit tests for <see cref="ConsolidationWorkItemDispatchService"/>.
/// Covers: lifecycle (leader-wait, poll-loop, leadership loss), CascadeFailureAsync paths,
/// non-fatal exception handling, constructor paths.
/// </summary>
public class ConsolidationWorkItemDispatchServiceTests
{
    // ── Lifecycle tests ──────────────────────────────────────────────────

    /// <summary>
    /// Verifies that PollAndDispatchConsolidationAsync is never called before leadership is acquired.
    /// Uses a negative-assertion pattern with fixed delay since there is no event to signal on.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_WaitsForLeadership_DoesNotPollUntilLeader()
    {
        // Arrange: leader election where IsLeader=false and LeaderToken never fires
        var leaderCts = new CancellationTokenSource();
        var leaderElectionMock = new Mock<ILeaderElectionService>();
        leaderElectionMock.SetupGet(l => l.IsLeader).Returns(false);
        leaderElectionMock.SetupGet(l => l.LeaderToken).Returns(leaderCts.Token);

        // Track if BuildStateAsync (and thus PollAndDispatchConsolidationAsync) is called
        var dbFactoryMock = new Mock<IDbContextFactory<PipelineDbContext>>();
        var dbFactoryCalled = false;
        dbFactoryMock
            .Setup(f => f.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .Callback(() => dbFactoryCalled = true)
            .ThrowsAsync(new InvalidOperationException("should not be called"));

        var handler = CreateHandlerWithLeaderElection(leaderElectionMock.Object, dbFactoryMock.Object);

        var hostStopCts = new CancellationTokenSource();

        // Act: start ExecuteAsync — it should remain in the 2s leader-wait loop
        var executeTask = InvokeExecuteAsync(handler, hostStopCts.Token);

        // Wait 300ms — enough to confirm no poll occurred (leader-wait loop is 2s)
        await Task.Delay(300);

        hostStopCts.Cancel();
        await WaitForTaskCompletion(executeTask);

        // Assert: db factory (and thus PollAndDispatchConsolidationAsync) was never called
        dbFactoryCalled.Should().BeFalse("handler must not poll before acquiring leadership");
    }

    /// <summary>
    /// Verifies that the poll loop stops promptly when the leadership token is cancelled.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_LeadershipLost_StopsPollLoop()
    {
        // Arrange: start as leader
        var leaderCts = new CancellationTokenSource();
        var leaderElectionMock = new Mock<ILeaderElectionService>();
        leaderElectionMock.SetupGet(l => l.IsLeader).Returns(true);
        leaderElectionMock.SetupGet(l => l.LeaderToken).Returns(leaderCts.Token);

        // Signal the first poll via a TCS wired to CreateDbContextAsync
        var firstPollTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var dbFactoryMock = new Mock<IDbContextFactory<PipelineDbContext>>();
        dbFactoryMock
            .Setup(f => f.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .Returns(async (CancellationToken ct) =>
            {
                firstPollTcs.TrySetResult(true);
                // Let the cancellation propagate naturally
                await Task.Delay(Timeout.Infinite, ct);
                return null!;
            });

        var handler = CreateHandlerWithLeaderElection(leaderElectionMock.Object, dbFactoryMock.Object);

        var hostStopCts = new CancellationTokenSource();
        var executeTask = InvokeExecuteAsync(handler, hostStopCts.Token);

        // Wait for at least one poll cycle to confirm entry into the poll loop
        var polled = await Task.WhenAny(firstPollTcs.Task, Task.Delay(5000));
        polled.Should().Be(firstPollTcs.Task, "should start polling after acquiring leadership");

        // Act: cancel leadership
        leaderCts.Cancel();
        leaderElectionMock.SetupGet(l => l.IsLeader).Returns(false);

        // Give brief time for the loop to exit, then stop the host
        await Task.Delay(200);
        hostStopCts.Cancel();

        var completed = await Task.WhenAny(executeTask, Task.Delay(5000));
        completed.Should().Be(executeTask, "ExecuteAsync should exit promptly after leadership loss + host stop");
    }

    /// <summary>
    /// Verifies that after leadership is re-acquired, polling resumes.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_LeadershipLostAndReacquired_ResumesPolling()
    {
        // Arrange: start as leader with a controllable token
        var firstLeaderCts = new CancellationTokenSource();
        var currentIsLeader = true;
        var currentLeaderToken = firstLeaderCts.Token;

        var leaderElectionMock = new Mock<ILeaderElectionService>();
        leaderElectionMock.SetupGet(l => l.IsLeader).Returns(() => currentIsLeader);
        leaderElectionMock.SetupGet(l => l.LeaderToken).Returns(() => currentLeaderToken);

        var pollCallCount = 0;
        var firstPollTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondPollTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        var dbFactoryMock = new Mock<IDbContextFactory<PipelineDbContext>>();
        dbFactoryMock
            .Setup(f => f.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .Returns(async (CancellationToken ct) =>
            {
                var count = Interlocked.Increment(ref pollCallCount);
                if (count == 1) firstPollTcs.TrySetResult(true);
                if (count >= 2) secondPollTcs.TrySetResult(true);
                await Task.Delay(Timeout.Infinite, ct);
                return null!;
            });

        var handler = CreateHandlerWithLeaderElection(leaderElectionMock.Object, dbFactoryMock.Object);

        var hostStopCts = new CancellationTokenSource();
        var executeTask = InvokeExecuteAsync(handler, hostStopCts.Token);

        // Wait for first poll to confirm we're in the loop
        var firstPollDone = await Task.WhenAny(firstPollTcs.Task, Task.Delay(5000));
        firstPollDone.Should().Be(firstPollTcs.Task, "should poll after initial leadership");

        // Simulate leadership loss
        currentIsLeader = false;
        firstLeaderCts.Cancel();
        await Task.Delay(200);

        // Re-acquire leadership with a new token
        var secondLeaderCts = new CancellationTokenSource();
        currentLeaderToken = secondLeaderCts.Token;
        currentIsLeader = true;

        // Wait for second poll — leader-wait loop checks every 2s, use 5s budget
        var secondPollDone = await Task.WhenAny(secondPollTcs.Task, Task.Delay(5000));
        secondPollDone.Should().Be(secondPollTcs.Task, "should resume polling after leadership re-acquisition");

        hostStopCts.Cancel();
        await WaitForTaskCompletion(executeTask);
    }


    [Fact]
    public async Task CascadeFailureAsync_WhenConsolidationServiceAvailable_DelegatesToService()
    {
        var mockService = new Mock<IConsolidationService>();
        mockService
            .Setup(s => s.UpdateRunAsync(
                It.IsAny<RunId>(), ConsolidationRunStatus.Failed, It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var handler = CreateHandler(consolidationService: mockService.Object);

        await handler.CascadeFailureAsync("run-001", "K8s job creation failed", CancellationToken.None);

        mockService.Verify(s => s.UpdateRunAsync(
            (RunId)"run-001",
            ConsolidationRunStatus.Failed,
            It.Is<string?>(msg => msg != null && msg.Contains("K8s job creation failed")),
            CancellationToken.None), Times.Once);
    }

    [Fact]
    public async Task CascadeFailureAsync_WhenServiceThrowsNonCancellation_IsNonFatal()
    {
        var mockService = new Mock<IConsolidationService>();
        mockService
            .Setup(s => s.UpdateRunAsync(
                It.IsAny<RunId>(), It.IsAny<ConsolidationRunStatus>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("DB connection lost"));

        var handler = CreateHandler(consolidationService: mockService.Object);

        // Must not throw — CascadeFailureAsync is always non-fatal
        await handler.Invoking(h => h.CascadeFailureAsync("run-fail", "dispatch error", CancellationToken.None))
            .Should().NotThrowAsync("CascadeFailureAsync must swallow non-cancellation exceptions");
    }

    [Fact]
    public async Task CascadeFailureAsync_WhenServiceUnavailable_UsesDirectStoreWrite()
    {
        // No IConsolidationService — falls back to direct store write
        var existingRun = new ConsolidationRun
        {
            RunId = "run-002",
            Status = ConsolidationRunStatus.Queued,
            Type = ConsolidationRunType.BrainConsolidation,
            StartedAtUtc = DateTimeOffset.UtcNow
        };

        var mockStore = new Mock<IConsolidationRunStore>();
        mockStore
            .Setup(s => s.GetByIdAsync((RunId)"run-002", It.IsAny<CancellationToken>()))
            .ReturnsAsync(existingRun);
        mockStore
            .Setup(s => s.SaveRunAsync(It.IsAny<ConsolidationRun>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var handler = CreateHandler(consolidationRunStore: mockStore.Object); // no consolidationService

        await handler.CascadeFailureAsync("run-002", "dispatch failed", CancellationToken.None);

        mockStore.Verify(s => s.SaveRunAsync(
            It.Is<ConsolidationRun>(r =>
                r.RunId == "run-002" &&
                r.Status == ConsolidationRunStatus.Failed &&
                r.Summary != null && r.Summary.Contains("dispatch failed")),
            CancellationToken.None), Times.Once);
    }

    [Fact]
    public async Task CascadeFailureAsync_WhenStoreRunAlreadyInTerminalState_DoesNotOverwrite()
    {
        // Run already Failed — direct-store path should not overwrite terminal state
        var existingRun = new ConsolidationRun
        {
            RunId = "run-003",
            Status = ConsolidationRunStatus.Failed, // already terminal
            Type = ConsolidationRunType.BrainConsolidation,
            StartedAtUtc = DateTimeOffset.UtcNow
        };

        var mockStore = new Mock<IConsolidationRunStore>();
        mockStore
            .Setup(s => s.GetByIdAsync((RunId)"run-003", It.IsAny<CancellationToken>()))
            .ReturnsAsync(existingRun);

        var handler = CreateHandler(consolidationRunStore: mockStore.Object);

        await handler.CascadeFailureAsync("run-003", "late failure", CancellationToken.None);

        mockStore.Verify(s => s.SaveRunAsync(
            It.IsAny<ConsolidationRun>(), It.IsAny<CancellationToken>()), Times.Never,
            "terminal-state run should not be overwritten");
    }

    [Fact]
    public async Task CascadeFailureAsync_WhenNeitherServiceNorStoreAvailable_IsNoOp()
    {
        var handler = CreateHandler(); // both null

        await handler.Invoking(h => h.CascadeFailureAsync("run-004", "failure", CancellationToken.None))
            .Should().NotThrowAsync("no-op when neither service nor store registered");
    }

    [Fact]
    public async Task CascadeFailureAsync_WhenStoreRunNotFound_IsNoOp()
    {
        var mockStore = new Mock<IConsolidationRunStore>();
        mockStore
            .Setup(s => s.GetByIdAsync(It.IsAny<RunId>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((ConsolidationRun?)null); // run not found in store

        var handler = CreateHandler(consolidationRunStore: mockStore.Object);

        await handler.Invoking(h => h.CascadeFailureAsync("run-ghost", "error", CancellationToken.None))
            .Should().NotThrowAsync("missing run in store should be a silent no-op");

        mockStore.Verify(s => s.SaveRunAsync(It.IsAny<ConsolidationRun>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task CascadeFailureAsync_WhenStoreRunQueued_TransitionsToFailed()
    {
        // Queued is one of the two allowed-overwrite states (Queued | Running)
        var existingRun = new ConsolidationRun
        {
            RunId = "run-005",
            Status = ConsolidationRunStatus.Queued,
            Type = ConsolidationRunType.RefactoringDetection,
            StartedAtUtc = DateTimeOffset.UtcNow
        };

        var mockStore = new Mock<IConsolidationRunStore>();
        mockStore
            .Setup(s => s.GetByIdAsync((RunId)"run-005", It.IsAny<CancellationToken>()))
            .ReturnsAsync(existingRun);

        ConsolidationRun? savedRun = null;
        mockStore
            .Setup(s => s.SaveRunAsync(It.IsAny<ConsolidationRun>(), It.IsAny<CancellationToken>()))
            .Callback<ConsolidationRun, CancellationToken>((r, _) => savedRun = r)
            .Returns(Task.CompletedTask);

        var handler = CreateHandler(consolidationRunStore: mockStore.Object);

        await handler.CascadeFailureAsync("run-005", "K8s error", CancellationToken.None);

        savedRun.Should().NotBeNull();
        savedRun!.Status.Should().Be(ConsolidationRunStatus.Failed);
        savedRun.CompletedAtUtc.Should().NotBeNull();
    }

    // ── Dispose / base-class coverage ────────────────────────────────────

    [Fact]
    public void Dispose_WithRateLimiter_DisposesWithoutThrowing()
    {
        // Exercises LeaderElectedPollingService.Dispose() which calls RateLimiter?.Dispose()
        // (lines 65-68 of LeaderElectedPollingService.cs).
        var handler = CreateHandler();
        // Must not throw — Dispose is called once and cleans up the TokenBucketRateLimiter.
        handler.Invoking(h => h.Dispose())
            .Should().NotThrow("Dispose must not throw regardless of RateLimiter state");
    }

    [Fact]
    public async Task ExecuteAsync_OnPollCycleThrowsUnhandledException_SwallowsAndContinues()
    {
        // Exercises the catch (Exception ex) path in LeaderElectedPollingService.RunLeadershipTermAsync
        // (lines 135-137) where an unhandled error in a poll cycle is logged and swallowed.
        var leaderCts = new CancellationTokenSource();
        var leaderElectionMock = new Mock<ILeaderElectionService>();
        leaderElectionMock.SetupGet(l => l.IsLeader).Returns(true);
        leaderElectionMock.SetupGet(l => l.LeaderToken).Returns(leaderCts.Token);

        var callCount = 0;
        var secondCallTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        var dbFactoryMock = new Mock<IDbContextFactory<PipelineDbContext>>();
        dbFactoryMock
            .Setup(f => f.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .Returns(async (CancellationToken ct) =>
            {
                var count = Interlocked.Increment(ref callCount);
                if (count == 1)
                {
                    // First poll: throw a non-cancellation exception to exercise the swallow path
                    throw new InvalidOperationException("simulated poll error");
                }
                // Second poll: signal and block to prevent further loops
                secondCallTcs.TrySetResult(true);
                await Task.Delay(Timeout.Infinite, ct);
                return null!;
            });

        var handler = CreateHandlerWithLeaderElection(leaderElectionMock.Object, dbFactoryMock.Object);

        var hostStopCts = new CancellationTokenSource();
        var executeTask = InvokeExecuteAsync(handler, hostStopCts.Token);

        // Wait for the second poll — proves the service continued after swallowing the first exception
        var secondPoll = await Task.WhenAny(secondCallTcs.Task, Task.Delay(5000));
        secondPoll.Should().Be(secondCallTcs.Task, "handler must continue polling after swallowing an unhandled poll exception");

        hostStopCts.Cancel();
        await WaitForTaskCompletion(executeTask);
    }

    // ── TransitionConsolidationRunToRunningAsync coverage ────────────────

    [Fact]
    public async Task TransitionConsolidationRunToRunningAsync_WhenServiceAvailable_DelegatesToService()
    {
        // Happy path: IConsolidationService is present; should call TransitionToRunningAsync.
        var mockService = new Mock<IConsolidationService>();
        mockService
            .Setup(s => s.TransitionToRunningAsync(It.IsAny<RunId>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var handler = CreateHandler(consolidationService: mockService.Object);

        var request = CreateMinimalRequest(runId: "run-tx-001");
        await handler.TransitionConsolidationRunToRunningAsync(request, CancellationToken.None);

        mockService.Verify(s => s.TransitionToRunningAsync(
            (RunId)"run-tx-001", CancellationToken.None), Times.Once);
    }

    [Fact]
    public async Task TransitionConsolidationRunToRunningAsync_WhenServiceThrows_IsNonFatal()
    {
        // Error path: IConsolidationService throws a non-cancellation exception; should be swallowed.
        var mockService = new Mock<IConsolidationService>();
        mockService
            .Setup(s => s.TransitionToRunningAsync(It.IsAny<RunId>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("DB unavailable"));

        var handler = CreateHandler(consolidationService: mockService.Object);
        var request = CreateMinimalRequest(runId: "run-tx-err");

        await handler.Invoking(h => h.TransitionConsolidationRunToRunningAsync(request, CancellationToken.None))
            .Should().NotThrowAsync("TransitionConsolidationRunToRunningAsync must swallow non-cancellation exceptions");
    }

    [Fact]
    public async Task TransitionConsolidationRunToRunningAsync_WhenNoRunId_IsNoOp()
    {
        // Edge case: request has no RunId and no IssueIdentifier — method returns early without calling anything.
        var mockService = new Mock<IConsolidationService>();
        var handler = CreateHandler(consolidationService: mockService.Object);

        var request = CreateMinimalRequest(runId: null, issueIdentifier: null);
        await handler.TransitionConsolidationRunToRunningAsync(request, CancellationToken.None);

        mockService.Verify(s => s.TransitionToRunningAsync(
            It.IsAny<RunId>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task TransitionConsolidationRunToRunningAsync_WhenServiceUnavailable_UsesDirectStoreWrite()
    {
        // Fallback path: no IConsolidationService; should transition run from Queued to Running via store.
        var existingRun = new ConsolidationRun
        {
            RunId = "run-tx-002",
            Status = ConsolidationRunStatus.Queued,
            Type = ConsolidationRunType.BrainConsolidation,
            StartedAtUtc = DateTimeOffset.UtcNow.AddMinutes(-5)
        };

        var mockStore = new Mock<IConsolidationRunStore>();
        mockStore
            .Setup(s => s.GetByIdAsync((RunId)"run-tx-002", It.IsAny<CancellationToken>()))
            .ReturnsAsync(existingRun);
        mockStore
            .Setup(s => s.SaveRunAsync(It.IsAny<ConsolidationRun>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var handler = CreateHandler(consolidationRunStore: mockStore.Object); // no consolidationService

        var request = CreateMinimalRequest(runId: "run-tx-002");
        await handler.TransitionConsolidationRunToRunningAsync(request, CancellationToken.None);

        mockStore.Verify(s => s.SaveRunAsync(
            It.Is<ConsolidationRun>(r =>
                r.RunId == "run-tx-002" &&
                r.Status == ConsolidationRunStatus.Running),
            CancellationToken.None), Times.Once);
    }

    [Fact]
    public async Task TransitionConsolidationRunToRunningAsync_WhenStoreRunNotQueued_DoesNotOverwrite()
    {
        // Direct-store path: run already Running; should not overwrite status.
        var existingRun = new ConsolidationRun
        {
            RunId = "run-tx-003",
            Status = ConsolidationRunStatus.Running, // already Running — not Queued
            Type = ConsolidationRunType.BrainConsolidation,
            StartedAtUtc = DateTimeOffset.UtcNow
        };

        var mockStore = new Mock<IConsolidationRunStore>();
        mockStore
            .Setup(s => s.GetByIdAsync((RunId)"run-tx-003", It.IsAny<CancellationToken>()))
            .ReturnsAsync(existingRun);

        var handler = CreateHandler(consolidationRunStore: mockStore.Object);
        var request = CreateMinimalRequest(runId: "run-tx-003");

        await handler.TransitionConsolidationRunToRunningAsync(request, CancellationToken.None);

        mockStore.Verify(s => s.SaveRunAsync(It.IsAny<ConsolidationRun>(), It.IsAny<CancellationToken>()),
            Times.Never, "should not overwrite a run that is already in Running state");
    }

    [Fact]
    public async Task TransitionConsolidationRunToRunningAsync_WhenStoreThrows_IsNonFatal()
    {
        // Direct-store path: store throws; should be swallowed.
        var mockStore = new Mock<IConsolidationRunStore>();
        mockStore
            .Setup(s => s.GetByIdAsync(It.IsAny<RunId>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("store failure"));

        var handler = CreateHandler(consolidationRunStore: mockStore.Object);
        var request = CreateMinimalRequest(runId: "run-tx-err2");

        await handler.Invoking(h => h.TransitionConsolidationRunToRunningAsync(request, CancellationToken.None))
            .Should().NotThrowAsync("must swallow exceptions from the direct-store fallback path");
    }

    [Fact]
    public async Task TransitionConsolidationRunToRunningAsync_WhenNoServiceAndNoStore_IsNoOp()
    {
        // No service, no store — method should return early without throwing.
        var handler = CreateHandler(); // both null
        var request = CreateMinimalRequest(runId: "run-tx-004");

        await handler.Invoking(h => h.TransitionConsolidationRunToRunningAsync(request, CancellationToken.None))
            .Should().NotThrowAsync("must be a silent no-op when neither service nor store registered");
    }

    [Fact]
    public async Task CascadeFailureAsync_WhenServiceThrowsOperationCanceled_IsNonFatal()
    {
        // OperationCanceledException thrown by IConsolidationService must be caught and swallowed
        // (treated as a graceful shutdown cancellation), not propagated to the caller.
        var mockService = new Mock<IConsolidationService>();
        mockService
            .Setup(s => s.UpdateRunAsync(
                It.IsAny<RunId>(), It.IsAny<ConsolidationRunStatus>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OperationCanceledException("shutdown"));

        var handler = CreateHandler(consolidationService: mockService.Object);

        await handler.Invoking(h => h.CascadeFailureAsync("run-oce-1", "error", CancellationToken.None))
            .Should().NotThrowAsync("OperationCanceledException from the service must be swallowed");
    }

    [Fact]
    public async Task CascadeFailureAsync_WhenStoreGetThrowsOperationCanceled_IsNonFatal()
    {
        // OperationCanceledException thrown by the direct-store fallback (GetByIdAsync) must be
        // caught and swallowed, not propagated to the caller.
        var mockStore = new Mock<IConsolidationRunStore>();
        mockStore
            .Setup(s => s.GetByIdAsync(It.IsAny<RunId>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OperationCanceledException("shutdown during store read"));

        var handler = CreateHandler(consolidationRunStore: mockStore.Object); // no consolidationService

        await handler.Invoking(h => h.CascadeFailureAsync("run-oce-2", "error", CancellationToken.None))
            .Should().NotThrowAsync("OperationCanceledException from the fallback store must be swallowed");
    }

    [Fact]
    public void Constructor_PublicDepsWithoutStateBuilder_ThrowsArgumentNullException()
    {
        // After removing the null-coalescing fallback, the public constructor requires StateBuilder.
        // Omitting it (null by default) must throw ArgumentNullException at construction time
        // rather than producing a silent second live DispatchStateBuilder instance.
        // Acceptance criterion 4.
        var dbFactoryMock = new Mock<IDbContextFactory<PipelineDbContext>>();
        var leaderElectionMock = new Mock<ILeaderElectionService>();
        var kubeClientMock = new Mock<IKubernetesJobClient>();

        var transitionService = new WorkItemTransitionService(
            dbFactoryMock.Object,
            NullLogger<WorkItemTransitionService>.Instance);

        var lifecycle = new DispatchLifecycleService(
            kubeClientMock.Object,
            transitionService,
            new DispatchServiceOptions());

        var configuration = new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build();

        var deps = new ConsolidationWorkItemDispatchServiceDependencies(
            dbFactoryMock.Object,
            leaderElectionMock.Object,
            lifecycle,
            JobTemplateStore.CreateEmpty(),
            configuration,
            TransitionService: transitionService);
        // StateBuilder intentionally omitted (null by default)

        var act = () => new ConsolidationWorkItemDispatchService(deps);
        act.Should().Throw<ArgumentNullException>()
            .WithParameterName("StateBuilder");
    }

    [Fact]
    public void Constructor_PublicDepsWithStateBuilder_UsesProvidedStateBuilder()
    {
        // Exercises the StateBuilder injection path in the public constructor:
        // when deps.StateBuilder is provided, the null-coalescing fallback is NOT taken.
        var dbFactoryMock = new Mock<IDbContextFactory<PipelineDbContext>>();
        var leaderElectionMock = new Mock<ILeaderElectionService>();
        var kubeClientMock = new Mock<IKubernetesJobClient>();

        var transitionService = new WorkItemTransitionService(
            dbFactoryMock.Object,
            NullLogger<WorkItemTransitionService>.Instance);

        var lifecycle = new DispatchLifecycleService(
            kubeClientMock.Object,
            transitionService,
            new DispatchServiceOptions());

        var configuration = new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build();

        var stateBuilder = new DispatchStateBuilder(
            dbFactoryMock.Object,
            lifecycle,
            JobTemplateStore.CreateEmpty(),
            new DispatchTemplateResolver(null, JobTemplateStore.CreateEmpty()),
            new DispatchServiceOptions());

        var deps = new ConsolidationWorkItemDispatchServiceDependencies(
            dbFactoryMock.Object,
            leaderElectionMock.Object,
            lifecycle,
            JobTemplateStore.CreateEmpty(),
            configuration,
            TransitionService: transitionService,
            StateBuilder: stateBuilder);

        var handler = new ConsolidationWorkItemDispatchService(deps);

        handler.Should().NotBeNull();
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    /// <summary>
    /// Invokes ExecuteAsync via BackgroundService reflection — matches the pattern in
    /// DispatchServiceLifecycleTests. Must NOT use IHostedService.StartAsync, which
    /// wraps exceptions differently and can mask failures.
    /// </summary>
    private static Task InvokeExecuteAsync(ConsolidationWorkItemDispatchService handler, CancellationToken stoppingToken)
    {
        var method = typeof(BackgroundService).GetMethod("ExecuteAsync",
            BindingFlags.NonPublic | BindingFlags.Instance);
        return (Task)method!.Invoke(handler, [stoppingToken])!;
    }

    private static async Task WaitForTaskCompletion(Task task)
    {
        try { await task.ConfigureAwait(false); }
        catch (OperationCanceledException) { /* expected on host stop */ }
    }

    /// <summary>
    /// Creates a ConsolidationWorkItemDispatchService with a controllable leader election and a
    /// provided IDbContextFactory mock. The factory is used by BuildStateAsync, so calls
    /// to CreateDbContextAsync reflect poll cycle invocations.
    /// </summary>
    private static ConsolidationWorkItemDispatchService CreateHandlerWithLeaderElection(
        ILeaderElectionService leaderElection,
        IDbContextFactory<PipelineDbContext> dbFactory)
    {
        var kubeClientMock = new Mock<IKubernetesJobClient>();

        var transitionService = new WorkItemTransitionService(
            dbFactory,
            NullLogger<WorkItemTransitionService>.Instance);

        var lifecycle = new DispatchLifecycleService(
            kubeClientMock.Object,
            transitionService,
            new DispatchServiceOptions());

        var options = new DispatchServiceOptions { PollIntervalSeconds = 1, RateLimitPerSecond = 100 };

        var stateBuilder = new DispatchStateBuilder(
            dbFactory,
            lifecycle,
            JobTemplateStore.CreateEmpty(),
            new DispatchTemplateResolver(null, JobTemplateStore.CreateEmpty()),
            options);

        return new ConsolidationWorkItemDispatchService(
            new ConsolidationWorkItemDispatchServiceDependencies(
                dbFactory,
                leaderElection,
                lifecycle,
                JobTemplateStore.CreateEmpty(),
                Mock.Of<IConfiguration>(),
                TransitionService: transitionService,
                StateBuilder: stateBuilder),
            options);
    }

    private static ConsolidationWorkItemDispatchService CreateHandler(
        IConsolidationService? consolidationService = null,
        IConsolidationRunStore? consolidationRunStore = null)
    {
        var dbFactoryMock = new Mock<IDbContextFactory<PipelineDbContext>>();
        var leaderElectionMock = new Mock<ILeaderElectionService>();
        var kubeClientMock = new Mock<IKubernetesJobClient>();

        var transitionService = new WorkItemTransitionService(
            dbFactoryMock.Object,
            NullLogger<WorkItemTransitionService>.Instance);

        var lifecycle = new DispatchLifecycleService(
            kubeClientMock.Object,
            transitionService,
            new DispatchServiceOptions());

        var options = new DispatchServiceOptions();

        var stateBuilder = new DispatchStateBuilder(
            dbFactoryMock.Object,
            lifecycle,
            JobTemplateStore.CreateEmpty(),
            new DispatchTemplateResolver(null, JobTemplateStore.CreateEmpty()),
            options);

        return new ConsolidationWorkItemDispatchService(
            new ConsolidationWorkItemDispatchServiceDependencies(
                dbFactoryMock.Object,
                leaderElectionMock.Object,
                lifecycle,
                JobTemplateStore.CreateEmpty(),
                Mock.Of<IConfiguration>(),
                TransitionService: transitionService,
                ConsolidationRunStore: consolidationRunStore,
                ConsolidationService: consolidationService,
                StateBuilder: stateBuilder),
            options);
    }

    /// <summary>
    /// Creates a minimal <see cref="JobDistributionRequest"/> for transition tests.
    /// Allows overriding <paramref name="runId"/> and <paramref name="issueIdentifier"/>.
    /// </summary>
    private static JobDistributionRequest CreateMinimalRequest(string? runId = "run-test", string? issueIdentifier = "owner/repo#1")
    {
        return new JobDistributionRequest
        {
            IssueIdentifier = issueIdentifier ?? string.Empty,
            IssueProviderConfigId = "provider-1",
            RepoProviderConfigId = "repo-1",
            InitiatedBy = "test",
            TaskType = WorkItemTaskType.Consolidation,
            AgentSelector = "kiro",
            TimeoutSeconds = 300,
            RunId = runId
        };
    }
}
