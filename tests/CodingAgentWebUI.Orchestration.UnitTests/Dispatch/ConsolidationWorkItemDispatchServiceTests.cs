using AwesomeAssertions;
using CodingAgentWebUI.Infrastructure.Persistence;
using CodingAgentWebUI.Infrastructure.Persistence.Services;
using CodingAgentWebUI.Orchestration.Dispatch;
using CodingAgentWebUI.Orchestration.LeaderElection;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace CodingAgentWebUI.Orchestration.UnitTests.Dispatch;

/// <summary>
/// Unit tests for <see cref="ConsolidationWorkItemDispatchService.CascadeFailureAsync"/>.
/// Covers: IConsolidationService path, direct IConsolidationRunStore fallback path,
/// non-fatal exception handling, and no-op when neither dependency is available.
/// </summary>
public class ConsolidationWorkItemDispatchServiceTests
{
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

    // ── Helpers ──────────────────────────────────────────────────────────

    [Fact]
    public async Task CascadeFailureAsync_WhenServiceThrowsOperationCanceled_IsNonFatal()
    {
        // Covers the OperationCanceledException catch path in the IConsolidationService branch (line 507-510)
        var mockService = new Mock<IConsolidationService>();
        mockService
            .Setup(s => s.UpdateRunAsync(
                It.IsAny<RunId>(), It.IsAny<ConsolidationRunStatus>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OperationCanceledException("simulated cancellation"));

        var handler = CreateHandler(consolidationService: mockService.Object);

        // Must not throw — OperationCanceledException during cascade is treated as non-fatal
        await handler.Invoking(h => h.CascadeFailureAsync("run-oce1", "error", CancellationToken.None))
            .Should().NotThrowAsync("OperationCanceledException from IConsolidationService cascade must be swallowed");
    }

    [Fact]
    public async Task CascadeFailureAsync_WhenStoreThrowsOperationCanceled_IsNonFatal()
    {
        // Covers the OperationCanceledException catch path in the direct-store fallback branch (line 537-540)
        var mockStore = new Mock<IConsolidationRunStore>();
        mockStore
            .Setup(s => s.GetByIdAsync(It.IsAny<RunId>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OperationCanceledException("simulated store cancellation"));

        var handler = CreateHandler(consolidationRunStore: mockStore.Object);

        await handler.Invoking(h => h.CascadeFailureAsync("run-oce2", "error", CancellationToken.None))
            .Should().NotThrowAsync("OperationCanceledException from direct-store cascade must be swallowed");
    }

    [Fact]
    public async Task CascadeFailureAsync_WhenStoreThrowsGeneralException_IsNonFatal()
    {
        // Covers the catch (Exception ex) path in the direct-store fallback branch (line 541-543)
        var mockStore = new Mock<IConsolidationRunStore>();
        mockStore
            .Setup(s => s.GetByIdAsync(It.IsAny<RunId>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new TimeoutException("store timeout"));

        var handler = CreateHandler(consolidationRunStore: mockStore.Object);

        await handler.Invoking(h => h.CascadeFailureAsync("run-ex3", "error", CancellationToken.None))
            .Should().NotThrowAsync("general exceptions from direct-store cascade must be swallowed");
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

        return new ConsolidationWorkItemDispatchService(
            new ConsolidationWorkItemDispatchServiceDependencies(
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
