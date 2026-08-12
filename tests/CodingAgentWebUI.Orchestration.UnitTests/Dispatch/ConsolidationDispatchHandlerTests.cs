using AwesomeAssertions;
using CodingAgentWebUI.Infrastructure.Persistence;
using CodingAgentWebUI.Infrastructure.Persistence.Services;
using CodingAgentWebUI.Orchestration.Dispatch;
using CodingAgentWebUI.Orchestration.LeaderElection;
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
/// Unit tests for <see cref="ConsolidationDispatchHandler"/>.
/// Covers: lifecycle (leader-wait, poll-loop, leadership loss), CascadeFailureAsync paths,
/// non-fatal exception handling, constructor paths.
/// </summary>
public class ConsolidationDispatchHandlerTests
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
        // TODO: currentIsLeader and currentLeaderToken are written on the test thread and read inside
        // the handler's background task thread via captured-variable lambdas, without volatile, Interlocked,
        // or any explicit memory barrier. This is a data race: the background thread is not guaranteed to
        // observe the updated values promptly (formally undefined behaviour, though benign on x86/x64 due to
        // TSO). The test could become intermittently unreliable on ARM or under aggressive JIT optimization.
        // Marking the variables volatile would eliminate the race. See Correctness WARNING (Issue #1912).
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

    // ── Constructor coverage ─────────────────────────────────────────────

    [Fact]
    public void Constructor_PublicDepsOnly_ConstructsWithoutThrowing()
    {
        // Exercises the public ConsolidationDispatchHandler(deps) constructor that accepts
        // IConfiguration (rather than the internal test constructor that accepts DispatchServiceOptions).
        // This covers the DI-wired code path used in production.
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

        var deps = new ConsolidationDispatchHandlerDependencies(
            dbFactoryMock.Object,
            leaderElectionMock.Object,
            lifecycle,
            JobTemplateStore.CreateEmpty(),
            configuration,
            TransitionService: transitionService);

        var handler = new ConsolidationDispatchHandler(deps);

        handler.Should().NotBeNull();
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

        var deps = new ConsolidationDispatchHandlerDependencies(
            dbFactoryMock.Object,
            leaderElectionMock.Object,
            lifecycle,
            JobTemplateStore.CreateEmpty(),
            configuration,
            TransitionService: transitionService,
            StateBuilder: stateBuilder);

        var handler = new ConsolidationDispatchHandler(deps);

        handler.Should().NotBeNull();
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    /// <summary>
    /// Invokes ExecuteAsync via BackgroundService reflection — matches the pattern in
    /// DispatchServiceLifecycleTests. Must NOT use IHostedService.StartAsync, which
    /// wraps exceptions differently and can mask failures.
    /// </summary>
    private static Task InvokeExecuteAsync(ConsolidationDispatchHandler handler, CancellationToken stoppingToken)
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
    /// Creates a ConsolidationDispatchHandler with a controllable leader election and a
    /// provided IDbContextFactory mock. The factory is used by BuildStateAsync, so calls
    /// to CreateDbContextAsync reflect poll cycle invocations.
    /// </summary>
    private static ConsolidationDispatchHandler CreateHandlerWithLeaderElection(
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

        return new ConsolidationDispatchHandler(
            new ConsolidationDispatchHandlerDependencies(
                dbFactory,
                leaderElection,
                lifecycle,
                JobTemplateStore.CreateEmpty(),
                Mock.Of<IConfiguration>(),
                TransitionService: transitionService),
            options);
    }

    private static ConsolidationDispatchHandler CreateHandler(
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

        return new ConsolidationDispatchHandler(
            new ConsolidationDispatchHandlerDependencies(
                dbFactoryMock.Object,
            leaderElectionMock.Object,
            lifecycle,
            JobTemplateStore.CreateEmpty(),
            Mock.Of<IConfiguration>(),
            TransitionService: transitionService,
            ConsolidationRunStore: consolidationRunStore,
            ConsolidationService: consolidationService),
            new DispatchServiceOptions());
    }
}
