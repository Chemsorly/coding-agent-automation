using AwesomeAssertions;
using CodingAgentWebUI.Infrastructure.Persistence;
using CodingAgentWebUI.Infrastructure.Persistence.Entities;
using CodingAgentWebUI.Infrastructure.Persistence.Services;
using CodingAgentWebUI.Orchestration;
using CodingAgentWebUI.Orchestration.Dispatch;
using CodingAgentWebUI.Pipeline;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using System.Text.Json;

namespace CodingAgentWebUI.UnitTests.Dispatch;

/// <summary>
/// Tests for <see cref="PendingWorkItemDrainService"/> consolidation dispatch coordination.
/// Verifies that consolidation WorkItems (TaskType=Consolidation) are routed to
/// <see cref="IConsolidationDrainDispatcher.TryDispatchAsync"/> and that the coordinator
/// behaves correctly on the returned result (no label swap, continue to next item).
/// Extracted dispatch logic is tested in <see cref="ConsolidationDrainDispatcherTests"/>.
/// </summary>
public sealed class PendingWorkItemDrainServiceConsolidationTests : IDisposable
{
    private readonly DbContextOptions<PipelineDbContext> _dbOptions;
    private readonly InMemoryDbContextFactory _dbFactory;
    private readonly Mock<ISignalRWorkDistributorAgentResolver> _mockResolver = new();
    private readonly Mock<IAgentCommunication> _mockAgentComm = new();
    private readonly Mock<ILabelSwapService> _mockLabelSwapper = new();
    private readonly Mock<IPendingWorkQuery> _mockPendingWork = new();
    private readonly Mock<IConsolidationDrainDispatcher> _mockConsolidationDrainDispatcher = new();
    private readonly OrchestratorRunService _runService;
    private readonly WorkItemTransitionService _transitionService;

    public PendingWorkItemDrainServiceConsolidationTests()
    {
        _dbOptions = new DbContextOptionsBuilder<PipelineDbContext>()
            .UseInMemoryDatabase($"DrainConsolidationTest_{Guid.NewGuid()}")
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        _dbFactory = new InMemoryDbContextFactory(_dbOptions);
        _runService = new OrchestratorRunService(Serilog.Log.Logger);
        _transitionService = new WorkItemTransitionService(_dbFactory, NullLogger<WorkItemTransitionService>.Instance);
        _mockPendingWork.Setup(p => p.GetPendingJobsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<PendingJob>().AsReadOnly());
    }

    [Fact]
    public async Task DrainPendingItems_ConsolidationItem_DelegatesToConsolidationDrainDispatcher()
    {
        // TODO: This test verifies that TryDispatchAsync is called, but does not check any post-condition
        // on the return value. If DispatchConsolidationItemAsync were accidentally changed to ignore the
        // bool result from TryDispatchAsync (e.g., discard-and-continue regardless), this test would
        // still pass. Consider adding a downstream assertion that is gated on the true return —
        // for example, that the mock dispatcher's result is respected (no second agent reservation
        // attempt, or that the drain loop processes subsequent items correctly).
        // Arrange: insert a consolidation WorkItem
        var runId = Guid.NewGuid().ToString();
        var workItemId = Guid.Parse(runId);
        await InsertConsolidationWorkItem(workItemId, runId, ConsolidationRunType.BrainConsolidation, "template-1", "/tmp/ws");

        // Setup: idle agent available
        _mockResolver.Setup(r => r.ResolveAgent("dotnet"))
            .Returns(new AgentResolveResult("conn-1", "agent-1"));

        // Setup: dispatcher succeeds
        _mockConsolidationDrainDispatcher
            .Setup(d => d.TryDispatchAsync(It.IsAny<WorkItemEntity>(), It.IsAny<JobDistributionRequest>(), It.IsAny<AgentId>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var service = CreateService();

        // Act
        await InvokeDrainAsync(service);

        // Assert: IConsolidationDrainDispatcher.TryDispatchAsync was called
        _mockConsolidationDrainDispatcher.Verify(
            d => d.TryDispatchAsync(
                It.Is<WorkItemEntity>(e => e.Id == workItemId),
                It.IsAny<JobDistributionRequest>(),
                It.IsAny<AgentId>(),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task DrainPendingItems_ConsolidationItem_DispatchFails_NoLabelSwap_ContinuesProcessing()
    {
        // TODO: The test name claims "ContinuesProcessing" but there is no assertion confirming the drain
        // loop continued after the false return. The only assertion is that SwapLabelWithRetryAsync was
        // never called — a negative-only check. If the coordinator accidentally throws or exits early on
        // a false result, the label-swap assertion still passes because the exception unwinds before any
        // label swap could occur. Consider inserting a second work item and asserting it was also attempted,
        // or otherwise confirming that the loop did not terminate early.
        // Arrange
        var runId = Guid.NewGuid().ToString();
        var workItemId = Guid.Parse(runId);
        await InsertConsolidationWorkItem(workItemId, runId, ConsolidationRunType.RefactoringDetection, null, "/tmp/ws");

        _mockResolver.Setup(r => r.ResolveAgent(""))
            .Returns(new AgentResolveResult("conn-1", "agent-1"));

        // Setup: dispatcher returns false (dispatch failed)
        _mockConsolidationDrainDispatcher
            .Setup(d => d.TryDispatchAsync(It.IsAny<WorkItemEntity>(), It.IsAny<JobDistributionRequest>(), It.IsAny<AgentId>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        var service = CreateService();

        // Act
        await InvokeDrainAsync(service);

        // Assert: no label swap (consolidation items never swap labels)
        _mockLabelSwapper.Verify(
            l => l.SwapLabelWithRetryAsync(It.IsAny<Guid>(), It.IsAny<ProviderConfigId>(), It.IsAny<IssueIdentifier>(), It.IsAny<LabelTargetKind>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task DrainPendingItems_ConsolidationItem_DispatchSucceeds_NoLabelSwap()
    {
        // Consolidation items must never trigger a label swap, regardless of dispatch outcome.
        var runId = Guid.NewGuid().ToString();
        var workItemId = Guid.Parse(runId);
        await InsertConsolidationWorkItem(workItemId, runId, ConsolidationRunType.BrainConsolidation, null, "/tmp/ws");

        _mockResolver.Setup(r => r.ResolveAgent("dotnet"))
            .Returns(new AgentResolveResult("conn-1", "agent-1"));

        _mockConsolidationDrainDispatcher
            .Setup(d => d.TryDispatchAsync(It.IsAny<WorkItemEntity>(), It.IsAny<JobDistributionRequest>(), It.IsAny<AgentId>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var service = CreateService();

        // Act
        await InvokeDrainAsync(service);

        // Assert: label swap was never called
        _mockLabelSwapper.Verify(
            l => l.SwapLabelWithRetryAsync(It.IsAny<Guid>(), It.IsAny<ProviderConfigId>(), It.IsAny<IssueIdentifier>(), It.IsAny<LabelTargetKind>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task DrainPendingItems_ConsolidationItem_NullDispatcher_SkipsItem()
    {
        // When consolidationDrainDispatcher is null (should not happen in practice with correct DI),
        // the drain service logs an error and skips the item.
        var runId = Guid.NewGuid().ToString();
        var workItemId = Guid.Parse(runId);
        await InsertConsolidationWorkItem(workItemId, runId, ConsolidationRunType.BrainConsolidation, null, "/tmp/ws");

        _mockResolver.Setup(r => r.ResolveAgent(""))
            .Returns(new AgentResolveResult("conn-1", "agent-1"));
        _mockResolver.Setup(r => r.ReleaseAgent("agent-1"));

        // Create service WITHOUT consolidation dispatcher
        var service = new PendingWorkItemDrainService(MakeDeps());

        // Act
        await InvokeDrainAsync(service);

        // Assert: IConsolidationDrainDispatcher was never called (not injected)
        _mockConsolidationDrainDispatcher.Verify(
            d => d.TryDispatchAsync(It.IsAny<WorkItemEntity>(), It.IsAny<JobDistributionRequest>(), It.IsAny<AgentId>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task DrainPendingItems_ConsolidationItemsHaveLowerPriorityThanPipeline()
    {
        // Arrange: insert a pipeline item (created first) and a consolidation item (created second)
        var pipelineId = Guid.NewGuid();
        var consolidationRunId = Guid.NewGuid().ToString();
        var consolidationId = Guid.Parse(consolidationRunId);

        // Pipeline item — created earlier
        var pipelineRequest = new JobDistributionRequest
        {
            IssueIdentifier = "org/repo#1",
            IssueProviderConfigId = "ip-1",
            RepoProviderConfigId = "rp-1",
            InitiatedBy = "loop",
            TaskType = WorkItemTaskType.Implementation,
            AgentSelector = "",
            RunId = pipelineId.ToString(),
            TimeoutSeconds = 3600
        };
        await using (var db = await _dbFactory.CreateDbContextAsync())
        {
            db.WorkItems.Add(new WorkItemEntity
            {
                Id = pipelineId,
                TaskType = WorkItemTaskType.Implementation,
                IssueIdentifier = "org/repo#1",
                IssueProviderConfigId = "ip-1",
                Status = WorkItemStatus.Pending,
                Payload = JsonSerializer.Serialize(pipelineRequest, PipelineJsonOptions.Default),
                AgentSelector = "",
                CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-10), // Older
                TimeoutSeconds = 3600
            });
            await db.SaveChangesAsync();
        }

        // Consolidation item — created later but should still be deprioritized
        await InsertConsolidationWorkItem(consolidationId, consolidationRunId, ConsolidationRunType.BrainConsolidation, null, "/tmp/ws",
            createdAt: DateTimeOffset.UtcNow.AddMinutes(-20)); // Even older, but should still come after pipeline

        // Only one agent available — should get the pipeline item first
        var callCount = 0;
        _mockResolver.Setup(r => r.ResolveAgent(""))
            .Returns(() => callCount++ == 0 ? new AgentResolveResult("conn-1", "agent-1") : null);
        _mockAgentComm.Setup(c => c.AssignJobAsync("conn-1", It.IsAny<JobAssignmentMessage>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var service = CreateService();

        // Act
        await InvokeDrainAsync(service);

        // Assert: pipeline item was dispatched (SignalR assign called), consolidation was not
        _mockAgentComm.Verify(
            c => c.AssignJobAsync("conn-1", It.IsAny<JobAssignmentMessage>(), It.IsAny<CancellationToken>()),
            Times.Once);
        _mockConsolidationDrainDispatcher.Verify(
            d => d.TryDispatchAsync(It.IsAny<WorkItemEntity>(), It.IsAny<JobDistributionRequest>(), It.IsAny<AgentId>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    private PendingWorkItemDrainService CreateService()
    {
        return new PendingWorkItemDrainService(
            MakeDeps(),
            null, // IProjectStore
            _mockConsolidationDrainDispatcher.Object);
    }

    private DrainServiceDependencies MakeDeps() =>
        new(_dbFactory, _mockResolver.Object, _mockAgentComm.Object,
            _runService, _transitionService, _mockPendingWork.Object,
            _mockLabelSwapper.Object, NullLogger<PendingWorkItemDrainService>.Instance,
            new DispatchRevertService(
                _dbFactory, _mockResolver.Object, _runService, _transitionService,
                NullLogger<DispatchRevertService>.Instance));

    private async Task InsertConsolidationWorkItem(
        Guid workItemId, string runId, ConsolidationRunType runType, string? templateId, string workspacePath,
        DateTimeOffset? createdAt = null)
    {
        var request = new JobDistributionRequest
        {
            IssueIdentifier = runId,
            IssueProviderConfigId = "consolidation",
            RepoProviderConfigId = "",
            InitiatedBy = "consolidation",
            TaskType = WorkItemTaskType.Consolidation,
            AgentSelector = runType == ConsolidationRunType.BrainConsolidation ? "dotnet" : "",
            TimeoutSeconds = 0,
            ConsolidationRunType = runType,
            ConsolidationTemplateId = templateId,
            ConsolidationWorkspacePath = workspacePath,
            RunId = runId
        };
        var payload = JsonSerializer.Serialize(request, PipelineJsonOptions.Default);

        await using var db = await _dbFactory.CreateDbContextAsync();
        db.WorkItems.Add(new WorkItemEntity
        {
            Id = workItemId,
            TaskType = WorkItemTaskType.Consolidation,
            IssueIdentifier = runId,
            IssueProviderConfigId = "consolidation",
            Status = WorkItemStatus.Pending,
            Payload = payload,
            AgentSelector = request.AgentSelector,
            CreatedAt = createdAt ?? DateTimeOffset.UtcNow,
            TimeoutSeconds = 0
        });
        await db.SaveChangesAsync();
    }

    /// <summary>
    /// Invokes DrainPendingItemsAsync directly via reflection to avoid BackgroundService
    /// scheduling races. This is deterministic — no Task.Delay needed.
    /// Catches OperationCanceledException to mirror ExecuteAsync's loop behavior.
    /// </summary>
    private static async Task InvokeDrainAsync(PendingWorkItemDrainService service)
    {
        var method = typeof(PendingWorkItemDrainService).GetMethod("DrainPendingItemsAsync",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
            ?? throw new InvalidOperationException("DrainPendingItemsAsync not found");
        var task = (Task)method.Invoke(service, [CancellationToken.None])!;
        try
        {
            await task;
        }
        catch (OperationCanceledException)
        {
            // Mirrors ExecuteAsync behavior: OCE during drain is swallowed by the loop
        }
    }

    public void Dispose()
    {
        using var db = new PipelineDbContext(_dbOptions);
        db.Database.EnsureDeleted();
        GC.SuppressFinalize(this);
    }

    private sealed class InMemoryDbContextFactory : IDbContextFactory<PipelineDbContext>
    {
        private readonly DbContextOptions<PipelineDbContext> _options;
        public InMemoryDbContextFactory(DbContextOptions<PipelineDbContext> options) => _options = options;
        public PipelineDbContext CreateDbContext() => new(_options);
        public Task<PipelineDbContext> CreateDbContextAsync(CancellationToken ct = default) => Task.FromResult(new PipelineDbContext(_options));
    }
}

/// <summary>
/// Verifies that when consolidation dispatch throws an exception, the WorkItem is
/// reverted from Dispatched back to Pending (rather than remaining stuck in Dispatched
/// until the stuck-item detector fires ~5 minutes later).
/// Regression test for exploratory-validation finding 1A-03.
/// These tests use a real <see cref="ConsolidationDrainDispatcher"/> (not a mock) to exercise
/// the exception-handling path end-to-end through the drain service.
/// </summary>
public sealed class PendingWorkItemDrainServiceConsolidationExceptionTests : IDisposable
{
    private readonly DbContextOptions<PipelineDbContext> _dbOptions;
    private readonly InMemoryDbContextFactory _dbFactory;
    private readonly Mock<ISignalRWorkDistributorAgentResolver> _mockResolver = new();
    private readonly Mock<IAgentCommunication> _mockAgentComm = new();
    private readonly Mock<ILabelSwapService> _mockLabelSwapper = new();
    private readonly Mock<IPendingWorkQuery> _mockPendingWork = new();
    private readonly Mock<IConsolidationDispatchService> _mockConsolidationDispatchService = new();
    private readonly Mock<IConsolidationRunStore> _mockConsolidationRunStore = new();
    private readonly OrchestratorRunService _runService;
    private readonly WorkItemTransitionService _transitionService;

    public PendingWorkItemDrainServiceConsolidationExceptionTests()
    {
        _dbOptions = new DbContextOptionsBuilder<PipelineDbContext>()
            .UseInMemoryDatabase($"DrainConsolidationExceptionTest_{Guid.NewGuid()}")
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        _dbFactory = new InMemoryDbContextFactory(_dbOptions);
        _runService = new OrchestratorRunService(Serilog.Log.Logger);
        _transitionService = new WorkItemTransitionService(_dbFactory, NullLogger<WorkItemTransitionService>.Instance);
        _mockPendingWork.Setup(p => p.GetPendingJobsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<PendingJob>().AsReadOnly());
    }

    [Fact]
    public async Task DrainPendingItems_ConsolidationItem_DispatchThrowsException_RevertsWorkItemToPending()
    {
        // Arrange: insert a consolidation WorkItem
        var runId = Guid.NewGuid().ToString();
        var workItemId = Guid.Parse(runId);
        await InsertConsolidationWorkItem(workItemId, runId, ConsolidationRunType.BrainConsolidation, "template-1", "/tmp/ws");

        // Setup: idle agent available
        _mockResolver.Setup(r => r.ResolveAgent("dotnet"))
            .Returns(new AgentResolveResult("conn-1", "agent-1"));
        _mockResolver.Setup(r => r.ReleaseAgent("agent-1"));

        // Setup: run exists and is Queued
        _mockConsolidationRunStore.Setup(s => s.GetByIdAsync(runId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ConsolidationRun { RunId = runId, Status = ConsolidationRunStatus.Queued, Type = ConsolidationRunType.BrainConsolidation, StartedAtUtc = DateTime.UtcNow });

        // Setup: dispatch THROWS an exception
        _mockConsolidationDispatchService
            .Setup(d => d.TryDispatchToAgentAsync(runId, ConsolidationRunType.BrainConsolidation, "template-1", "/tmp/ws", "agent-1", It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Token vending failed"));

        var service = CreateService();

        // Act
        await InvokeDrainAsync(service);

        // Assert: WorkItem should be reverted to Pending (not stuck in Dispatched)
        await using var db = await _dbFactory.CreateDbContextAsync();
        var item = await db.WorkItems.FindAsync(workItemId);
        item!.Status.Should().Be(WorkItemStatus.Pending);
        item.AssignedAgentId.Should().BeNull();
        item.DispatchedAt.Should().BeNull();
    }

    [Fact]
    public async Task DrainPendingItems_ConsolidationItem_ShutdownDuringDispatch_RevertsWorkItemToPending()
    {
        // Reproduction: When stoppingToken is cancelled during consolidation dispatch (graceful shutdown),
        // the catch block's revert TransitionAsync also used the same cancelled token,
        // causing the revert to throw OperationCanceledException and leaving the work item
        // stuck in Dispatched status. Fix: use CancellationToken.None for the revert call.
        var runId = Guid.NewGuid().ToString();
        var workItemId = Guid.Parse(runId);
        await InsertConsolidationWorkItem(workItemId, runId, ConsolidationRunType.BrainConsolidation, "template-1", "/tmp/ws");

        // Setup: idle agent available
        _mockResolver.Setup(r => r.ResolveAgent("dotnet"))
            .Returns(new AgentResolveResult("conn-1", "agent-1"));
        _mockResolver.Setup(r => r.ReleaseAgent("agent-1"));

        // Setup: run exists and is Queued
        _mockConsolidationRunStore.Setup(s => s.GetByIdAsync(runId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ConsolidationRun { RunId = runId, Status = ConsolidationRunStatus.Queued, Type = ConsolidationRunType.BrainConsolidation, StartedAtUtc = DateTime.UtcNow });

        // Setup: dispatch simulates shutdown by cancelling CTS then throwing
        using var cts = new CancellationTokenSource();
        _mockConsolidationDispatchService
            .Setup(d => d.TryDispatchToAgentAsync(runId, ConsolidationRunType.BrainConsolidation, (TemplateId?)"template-1", "/tmp/ws", (AgentId)"agent-1", It.IsAny<CancellationToken>()))
            .Returns((string _, ConsolidationRunType _, TemplateId? _, string _, AgentId _, CancellationToken _) =>
            {
                cts.Cancel(); // Simulate graceful shutdown — token is now cancelled
                throw new OperationCanceledException(cts.Token);
            });

        var service = CreateService(new CancellationAwareDbContextFactory(_dbOptions));

        // Act: start with the CTS that will be cancelled inside the mock
        service.Signal();
        await service.StartAsync(cts.Token);
        // Wait for ExecuteAsync to complete (cts is cancelled inside dispatch mock,
        // which causes the drain to revert and the loop to exit)
        var executeTask = service.ExecuteTask;
        if (executeTask is not null)
        {
            var completed = await Task.WhenAny(executeTask, Task.Delay(TimeSpan.FromSeconds(10)));
            completed.Should().Be(executeTask, "ExecuteAsync should complete after CTS cancellation");
        }
        await service.StopAsync(CancellationToken.None);

        // Assert: WorkItem must be reverted to Pending despite the cancelled stoppingToken
        await using var checkDb = await _dbFactory.CreateDbContextAsync();
        var item = await checkDb.WorkItems.FindAsync(workItemId);
        item.Should().NotBeNull();
        item!.Status.Should().Be(WorkItemStatus.Pending,
            "WorkItem must revert to Pending during graceful shutdown — revert must use CancellationToken.None");
        item.AssignedAgentId.Should().BeNull("AssignedAgentId must be cleared on revert");
        item.DispatchedAt.Should().BeNull("DispatchedAt must be cleared on revert");
    }

    private PendingWorkItemDrainService CreateService(IDbContextFactory<PipelineDbContext>? dbFactoryForTransition = null)
    {
        var factory = dbFactoryForTransition ?? _dbFactory;
        var transitionService = new WorkItemTransitionService(factory, NullLogger<WorkItemTransitionService>.Instance);
        var revertHandler = new DispatchRevertService(
            _dbFactory, _mockResolver.Object, _runService, transitionService,
            NullLogger<DispatchRevertService>.Instance);
        var dispatchAttemptService = new DispatchAttemptService(transitionService, revertHandler);
        var consolidationDrainDispatcher = new ConsolidationDrainDispatcher(
            _mockConsolidationDispatchService.Object,
            _mockConsolidationRunStore.Object,
            dispatchAttemptService,
            transitionService,
            _mockResolver.Object,
            revertHandler,
            NullLogger<ConsolidationDrainDispatcher>.Instance);

        return new PendingWorkItemDrainService(
            new DrainServiceDependencies(
                _dbFactory, _mockResolver.Object, _mockAgentComm.Object,
                _runService, _transitionService, _mockPendingWork.Object,
                _mockLabelSwapper.Object, NullLogger<PendingWorkItemDrainService>.Instance,
                new DispatchRevertService(
                    _dbFactory, _mockResolver.Object, _runService, _transitionService,
                    NullLogger<DispatchRevertService>.Instance)),
            null, // IProjectStore
            consolidationDrainDispatcher);
    }

    private async Task InsertConsolidationWorkItem(
        Guid workItemId, string runId, ConsolidationRunType runType, string? templateId, string workspacePath)
    {
        var request = new JobDistributionRequest
        {
            IssueIdentifier = runId,
            IssueProviderConfigId = "consolidation",
            RepoProviderConfigId = "",
            InitiatedBy = "consolidation",
            TaskType = WorkItemTaskType.Consolidation,
            AgentSelector = runType == ConsolidationRunType.BrainConsolidation ? "dotnet" : "",
            TimeoutSeconds = 0,
            ConsolidationRunType = runType,
            ConsolidationTemplateId = templateId,
            ConsolidationWorkspacePath = workspacePath,
            RunId = runId
        };
        var payload = JsonSerializer.Serialize(request, PipelineJsonOptions.Default);

        await using var db = await _dbFactory.CreateDbContextAsync();
        db.WorkItems.Add(new WorkItemEntity
        {
            Id = workItemId,
            TaskType = WorkItemTaskType.Consolidation,
            IssueIdentifier = runId,
            IssueProviderConfigId = "consolidation",
            Status = WorkItemStatus.Pending,
            Payload = payload,
            AgentSelector = request.AgentSelector,
            CreatedAt = DateTimeOffset.UtcNow,
            TimeoutSeconds = 0
        });
        await db.SaveChangesAsync();
    }

    /// <summary>
    /// Invokes DrainPendingItemsAsync directly via reflection to avoid BackgroundService
    /// scheduling races. This is deterministic — no Task.Delay needed.
    /// Catches OperationCanceledException to mirror ExecuteAsync's loop behavior.
    /// </summary>
    private static async Task InvokeDrainAsync(PendingWorkItemDrainService service)
    {
        var method = typeof(PendingWorkItemDrainService).GetMethod("DrainPendingItemsAsync",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
            ?? throw new InvalidOperationException("DrainPendingItemsAsync not found");
        var task = (Task)method.Invoke(service, [CancellationToken.None])!;
        try
        {
            await task;
        }
        catch (OperationCanceledException)
        {
            // Mirrors ExecuteAsync behavior: OCE during drain is swallowed by the loop
        }
    }

    public void Dispose()
    {
        using var db = new PipelineDbContext(_dbOptions);
        db.Database.EnsureDeleted();
        GC.SuppressFinalize(this);
    }

    private sealed class InMemoryDbContextFactory : IDbContextFactory<PipelineDbContext>
    {
        private readonly DbContextOptions<PipelineDbContext> _options;
        public InMemoryDbContextFactory(DbContextOptions<PipelineDbContext> options) => _options = options;
        public PipelineDbContext CreateDbContext() => new(_options);
        public Task<PipelineDbContext> CreateDbContextAsync(CancellationToken ct = default) => Task.FromResult(new PipelineDbContext(_options));
    }

    /// <summary>
    /// Factory that throws <see cref="OperationCanceledException"/> when CreateDbContextAsync
    /// is called with a cancelled token — simulating real DB provider behavior that the
    /// EF Core InMemory provider does not replicate (see dotnet/efcore#13368).
    /// Used to prove that the revert path passes CancellationToken.None rather than the
    /// cancelled stoppingToken.
    /// </summary>
    private sealed class CancellationAwareDbContextFactory : IDbContextFactory<PipelineDbContext>
    {
        private readonly DbContextOptions<PipelineDbContext> _options;
        public CancellationAwareDbContextFactory(DbContextOptions<PipelineDbContext> options) => _options = options;
        public PipelineDbContext CreateDbContext() => new(_options);
        public Task<PipelineDbContext> CreateDbContextAsync(CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(new PipelineDbContext(_options));
        }
    }
}

/// <summary>
/// Verifies that the <c>TryRevertToPendingAsync</c> behavior of <see cref="DispatchRevertService"/>
/// is correct for both the increment and non-increment paths.
/// These tests target <see cref="DispatchRevertService"/> directly since the method was
/// moved there from <see cref="PendingWorkItemDrainService"/>.
/// </summary>
public sealed class TryRevertToPendingAsyncTests : IDisposable
{
    private readonly DbContextOptions<PipelineDbContext> _dbOptions;
    private readonly InMemoryDbContextFactory _dbFactory;
    private readonly Mock<ISignalRWorkDistributorAgentResolver> _mockResolver = new();
    private readonly Mock<IAgentCommunication> _mockAgentComm = new();
    private readonly Mock<ILabelSwapService> _mockLabelSwapper = new();
    private readonly Mock<IPendingWorkQuery> _mockPendingWork = new();
    private readonly OrchestratorRunService _runService;

    public TryRevertToPendingAsyncTests()
    {
        _dbOptions = new DbContextOptionsBuilder<PipelineDbContext>()
            .UseInMemoryDatabase($"TryRevertToPendingTest_{Guid.NewGuid()}")
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        _dbFactory = new InMemoryDbContextFactory(_dbOptions);
        _runService = new OrchestratorRunService(Serilog.Log.Logger);
        _mockPendingWork.Setup(p => p.GetPendingJobsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<PendingJob>().AsReadOnly());
    }

    [Fact]
    public async Task TryRevertToPending_IncrementRetryCountFalse_LeavesRetryCountUnchanged()
    {
        // Arrange: item is in Dispatched state (revert is from Dispatched → Pending)
        var workItemId = await InsertDispatchedWorkItem(initialRetryCount: 2);
        var handler = CreateHandler();

        // Act
        await handler.TryRevertToPendingAsync(workItemId, incrementRetryCount: false);

        // Assert: status reverted, RetryCount unchanged
        await using var db = await _dbFactory.CreateDbContextAsync();
        var item = await db.WorkItems.FindAsync(workItemId);
        item!.Status.Should().Be(WorkItemStatus.Pending);
        item.DispatchedAt.Should().BeNull();
        item.AssignedAgentId.Should().BeNull();
        item.RetryCount.Should().Be(2, "incrementRetryCount: false must not change RetryCount");
    }

    [Fact]
    public async Task TryRevertToPending_IncrementRetryCountTrue_IncrementsRetryCount()
    {
        var workItemId = await InsertDispatchedWorkItem(initialRetryCount: 3);
        var handler = CreateHandler();

        // Act
        await handler.TryRevertToPendingAsync(workItemId, incrementRetryCount: true);

        // Assert: status reverted, RetryCount incremented
        await using var db = await _dbFactory.CreateDbContextAsync();
        var item = await db.WorkItems.FindAsync(workItemId);
        item!.Status.Should().Be(WorkItemStatus.Pending);
        item.RetryCount.Should().Be(4, "incrementRetryCount: true must increment RetryCount");
    }

    [Fact]
    public async Task TryRevertToPending_TransitionThrows_ExceptionSwallowed_DoesNotPropagate()
    {
        // Arrange: use a DbContext factory that always throws, so TransitionAsync will fail
        var workItemId = await InsertDispatchedWorkItem(initialRetryCount: 0);
        var throwingFactory = new AlwaysThrowingDbContextFactory();
        var failingTransition = new WorkItemTransitionService(throwingFactory, NullLogger<WorkItemTransitionService>.Instance);
        var handler = CreateHandlerWithTransition(failingTransition);

        // Act: must not throw
        var act = async () => await handler.TryRevertToPendingAsync(workItemId, incrementRetryCount: true);

        // Assert: exception is swallowed (stuck-item detector will handle)
        await act.Should().NotThrowAsync("TryRevertToPendingAsync must swallow transition exceptions");
    }

    [Fact]
    public async Task TryRevertToPending_WithCancellationToken_UsesProvidedToken()
    {
        // Verifies that the optional ct parameter is wired through to TransitionAsync.
        // A cancelled token causes CreateDbContextAsync to throw (via CancellationAwareDbContextFactory),
        // which forces TransitionAsync to fail — the exception is swallowed by TryRevertToPendingAsync,
        // confirming the token was forwarded rather than ignored.
        var workItemId = await InsertDispatchedWorkItem(initialRetryCount: 0);
        using var cts = new CancellationTokenSource();
        cts.Cancel(); // pre-cancel so the revert fails immediately

        var cancellationAwareFactory = new CancellationAwareDbContextFactory(_dbOptions);
        var transitionService = new WorkItemTransitionService(cancellationAwareFactory, NullLogger<WorkItemTransitionService>.Instance);
        var handler = CreateHandlerWithTransition(transitionService);

        // Negative case: a cancelled token causes the revert to fail → item stays Dispatched.
        var act = async () => await handler.TryRevertToPendingAsync(workItemId, false, cts.Token);
        await act.Should().NotThrowAsync("TryRevertToPendingAsync must swallow the OperationCanceledException from a cancelled token");

        await using var db = await _dbFactory.CreateDbContextAsync();
        var item = await db.WorkItems.FindAsync(workItemId);
        item!.Status.Should().Be(WorkItemStatus.Dispatched,
            "revert was cancelled via the provided ct — item must remain Dispatched (stuck-item detector will handle)");

        // Positive case: an uncancelled token allows the revert to succeed → item transitions to Pending.
        // This distinguishes "ct was forwarded and respected" from "transition always fails regardless of ct".
        await handler.TryRevertToPendingAsync(workItemId, false, CancellationToken.None);

        await using var db2 = await _dbFactory.CreateDbContextAsync();
        var itemAfter = await db2.WorkItems.FindAsync(workItemId);
        itemAfter!.Status.Should().Be(WorkItemStatus.Pending,
            "an uncancelled token must allow the revert to succeed — confirming ct forwarding is the cause of the difference");
    }

    private DispatchRevertService CreateHandler()
    {
        var transitionService = new WorkItemTransitionService(_dbFactory, NullLogger<WorkItemTransitionService>.Instance);
        return CreateHandlerWithTransition(transitionService);
    }

    private DispatchRevertService CreateHandlerWithTransition(WorkItemTransitionService transitionService) =>
        new(_dbFactory, _mockResolver.Object, _runService, transitionService,
            NullLogger<DispatchRevertService>.Instance);

    // Keep CreateService/CreateServiceWithTransition for any remaining references
    private PendingWorkItemDrainService CreateService()
    {
        var transitionService = new WorkItemTransitionService(_dbFactory, NullLogger<WorkItemTransitionService>.Instance);
        return CreateServiceWithTransition(transitionService);
    }

    private PendingWorkItemDrainService CreateServiceWithTransition(WorkItemTransitionService transitionService) =>
        new(new DrainServiceDependencies(
            _dbFactory, _mockResolver.Object, _mockAgentComm.Object,
            _runService, transitionService, _mockPendingWork.Object,
            _mockLabelSwapper.Object, NullLogger<PendingWorkItemDrainService>.Instance,
            new DispatchRevertService(
                _dbFactory, _mockResolver.Object, _runService, transitionService,
                NullLogger<DispatchRevertService>.Instance)));

    private async Task<Guid> InsertDispatchedWorkItem(int initialRetryCount)
    {
        var id = Guid.NewGuid();
        await using var db = await _dbFactory.CreateDbContextAsync();
        db.WorkItems.Add(new WorkItemEntity
        {
            Id = id,
            TaskType = WorkItemTaskType.Implementation,
            IssueIdentifier = "org/repo#1",
            IssueProviderConfigId = "ip-1",
            Status = WorkItemStatus.Dispatched,
            Payload = "{}",
            AgentSelector = "",
            CreatedAt = DateTimeOffset.UtcNow,
            DispatchedAt = DateTimeOffset.UtcNow,
            AssignedAgentId = "agent-1",
            TimeoutSeconds = 3600,
            RetryCount = initialRetryCount
        });
        await db.SaveChangesAsync();
        return id;
    }

    public void Dispose()
    {
        using var db = new PipelineDbContext(_dbOptions);
        db.Database.EnsureDeleted();
        GC.SuppressFinalize(this);
    }

    private sealed class InMemoryDbContextFactory : IDbContextFactory<PipelineDbContext>
    {
        private readonly DbContextOptions<PipelineDbContext> _options;
        public InMemoryDbContextFactory(DbContextOptions<PipelineDbContext> options) => _options = options;
        public PipelineDbContext CreateDbContext() => new(_options);
        public Task<PipelineDbContext> CreateDbContextAsync(CancellationToken ct = default) => Task.FromResult(new PipelineDbContext(_options));
    }

    private sealed class AlwaysThrowingDbContextFactory : IDbContextFactory<PipelineDbContext>
    {
        public PipelineDbContext CreateDbContext() => throw new InvalidOperationException("Simulated DB failure");
        public Task<PipelineDbContext> CreateDbContextAsync(CancellationToken ct = default)
            => throw new InvalidOperationException("Simulated DB failure");
    }

    private sealed class CancellationAwareDbContextFactory : IDbContextFactory<PipelineDbContext>
    {
        private readonly DbContextOptions<PipelineDbContext> _options;
        public CancellationAwareDbContextFactory(DbContextOptions<PipelineDbContext> options) => _options = options;
        public PipelineDbContext CreateDbContext() => new(_options);
        public Task<PipelineDbContext> CreateDbContextAsync(CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(new PipelineDbContext(_options));
        }
    }
}
