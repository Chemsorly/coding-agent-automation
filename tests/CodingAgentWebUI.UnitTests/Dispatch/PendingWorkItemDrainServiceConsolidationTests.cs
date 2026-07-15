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
/// Tests for <see cref="PendingWorkItemDrainService"/> consolidation dispatch path.
/// Verifies that consolidation WorkItems (TaskType=Consolidation) are dispatched via
/// <see cref="IConsolidationDispatcher.TryDispatchToAgentAsync"/> with token vending at drain time.
/// </summary>
public sealed class PendingWorkItemDrainServiceConsolidationTests : IDisposable
{
    private readonly DbContextOptions<PipelineDbContext> _dbOptions;
    private readonly InMemoryDbContextFactory _dbFactory;
    private readonly Mock<ISignalRWorkDistributorAgentResolver> _mockResolver = new();
    private readonly Mock<IAgentCommunication> _mockAgentComm = new();
    private readonly Mock<ILabelSwapper> _mockLabelSwapper = new();
    private readonly Mock<IPendingWorkQuery> _mockPendingWork = new();
    private readonly Mock<IConsolidationDispatcher> _mockConsolidationDispatcher = new();
    private readonly Mock<IConsolidationRunStore> _mockConsolidationRunStore = new();
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
    public async Task DrainPendingItems_ConsolidationItem_DispatchesViaTryDispatchToAgentAsync()
    {
        // Arrange: insert a consolidation WorkItem
        var runId = Guid.NewGuid().ToString();
        var workItemId = Guid.Parse(runId);
        await InsertConsolidationWorkItem(workItemId, runId, ConsolidationRunType.BrainConsolidation, "template-1", "/tmp/ws");

        // Setup: idle agent available
        _mockResolver.Setup(r => r.ResolveAgent("dotnet"))
            .Returns(new AgentResolveResult("conn-1", "agent-1"));

        // Setup: run exists and is Queued
        _mockConsolidationRunStore.Setup(s => s.GetByIdAsync(runId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ConsolidationRun { RunId = runId, Status = ConsolidationRunStatus.Queued, Type = ConsolidationRunType.BrainConsolidation, StartedAtUtc = DateTime.UtcNow });

        // Setup: dispatch succeeds (token vending happens inside TryDispatchToAgentAsync)
        _mockConsolidationDispatcher
            .Setup(d => d.TryDispatchToAgentAsync(runId, ConsolidationRunType.BrainConsolidation, "template-1", "/tmp/ws", "agent-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var service = CreateService();

        // Act
        await InvokeDrainAsync(service);

        // Assert: TryDispatchToAgentAsync was called (token vending occurs within)
        _mockConsolidationDispatcher.Verify(
            d => d.TryDispatchToAgentAsync(runId, ConsolidationRunType.BrainConsolidation, "template-1", "/tmp/ws", "agent-1", It.IsAny<CancellationToken>()),
            Times.Once);

        // Assert: WorkItem transitioned to Dispatched
        await using var db = await _dbFactory.CreateDbContextAsync();
        var item = await db.WorkItems.FindAsync(workItemId);
        item!.Status.Should().Be(WorkItemStatus.Dispatched);
        item.AssignedAgentId.Should().Be("agent-1");
    }

    [Fact]
    public async Task DrainPendingItems_ConsolidationItem_DispatchFails_RevertsToPending()
    {
        // Arrange
        var runId = Guid.NewGuid().ToString();
        var workItemId = Guid.Parse(runId);
        await InsertConsolidationWorkItem(workItemId, runId, ConsolidationRunType.RefactoringDetection, null, "/tmp/ws");

        _mockResolver.Setup(r => r.ResolveAgent(""))
            .Returns(new AgentResolveResult("conn-1", "agent-1"));
        _mockConsolidationRunStore.Setup(s => s.GetByIdAsync(runId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ConsolidationRun { RunId = runId, Status = ConsolidationRunStatus.Queued, Type = ConsolidationRunType.RefactoringDetection, StartedAtUtc = DateTime.UtcNow });
        _mockConsolidationDispatcher
            .Setup(d => d.TryDispatchToAgentAsync(runId, ConsolidationRunType.RefactoringDetection, null, "/tmp/ws", "agent-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(false); // Dispatch failed

        _mockResolver.Setup(r => r.ReleaseAgent("agent-1"));

        var service = CreateService();

        // Act
        await InvokeDrainAsync(service);

        // Assert: WorkItem reverted to Pending (available for next drain cycle)
        await using var db = await _dbFactory.CreateDbContextAsync();
        var item = await db.WorkItems.FindAsync(workItemId);
        item!.Status.Should().Be(WorkItemStatus.Pending);
        item.AssignedAgentId.Should().BeNull();
        item.DispatchedAt.Should().BeNull();
    }

    [Fact]
    public async Task DrainPendingItems_ConsolidationItem_CancelledRun_TransitionsWorkItemToCancelled()
    {
        // Arrange: insert a consolidation WorkItem for a cancelled run
        var runId = Guid.NewGuid().ToString();
        var workItemId = Guid.Parse(runId);
        await InsertConsolidationWorkItem(workItemId, runId, ConsolidationRunType.BrainConsolidation, null, "/tmp/ws");

        _mockResolver.Setup(r => r.ResolveAgent("dotnet"))
            .Returns(new AgentResolveResult("conn-1", "agent-1"));

        // Run was cancelled while queued
        _mockConsolidationRunStore.Setup(s => s.GetByIdAsync(runId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ConsolidationRun { RunId = runId, Status = ConsolidationRunStatus.Cancelled, Type = ConsolidationRunType.BrainConsolidation, StartedAtUtc = DateTime.UtcNow });

        _mockResolver.Setup(r => r.ReleaseAgent("agent-1"));

        var service = CreateService();

        // Act
        await InvokeDrainAsync(service);

        // Assert: WorkItem transitioned to Cancelled, dispatch never called
        await using var db = await _dbFactory.CreateDbContextAsync();
        var item = await db.WorkItems.FindAsync(workItemId);
        item!.Status.Should().Be(WorkItemStatus.Cancelled);
        item.CompletedAt.Should().NotBeNull();

        _mockConsolidationDispatcher.Verify(
            d => d.TryDispatchToAgentAsync(It.IsAny<string>(), It.IsAny<ConsolidationRunType>(), It.IsAny<string?>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task DrainPendingItems_ConsolidationItem_NullRunStore_SkipsItem()
    {
        // When consolidationRunStore is null (should not happen in practice with correct DI),
        // the drain service logs an error and skips the item.
        var runId = Guid.NewGuid().ToString();
        var workItemId = Guid.Parse(runId);
        await InsertConsolidationWorkItem(workItemId, runId, ConsolidationRunType.BrainConsolidation, null, "/tmp/ws");

        _mockResolver.Setup(r => r.ResolveAgent(""))
            .Returns(new AgentResolveResult("conn-1", "agent-1"));
        _mockResolver.Setup(r => r.ReleaseAgent("agent-1"));

        // Create service WITHOUT consolidation dependencies
        var service = new PendingWorkItemDrainService(
            _dbFactory,
            _mockResolver.Object,
            _mockAgentComm.Object,
            _runService,
            _transitionService,
            _mockPendingWork.Object,
            _mockLabelSwapper.Object,
            NullLogger<PendingWorkItemDrainService>.Instance);

        // Act
        await InvokeDrainAsync(service);

        // Assert: item remains Pending (not dispatched)
        await using var db = await _dbFactory.CreateDbContextAsync();
        var item = await db.WorkItems.FindAsync(workItemId);
        item!.Status.Should().Be(WorkItemStatus.Pending);

        // Dispatch was never attempted
        _mockConsolidationDispatcher.Verify(
            d => d.TryDispatchToAgentAsync(It.IsAny<string>(), It.IsAny<ConsolidationRunType>(), It.IsAny<string?>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
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

        _mockConsolidationRunStore.Setup(s => s.GetByIdAsync(consolidationRunId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ConsolidationRun { RunId = consolidationRunId, Status = ConsolidationRunStatus.Queued, Type = ConsolidationRunType.BrainConsolidation, StartedAtUtc = DateTime.UtcNow });

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
        _mockConsolidationDispatcher.Verify(
            d => d.TryDispatchToAgentAsync(It.IsAny<string>(), It.IsAny<ConsolidationRunType>(), It.IsAny<string?>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    private PendingWorkItemDrainService CreateService()
    {
        return new PendingWorkItemDrainService(
            _dbFactory,
            _mockResolver.Object,
            _mockAgentComm.Object,
            _runService,
            _transitionService,
            _mockPendingWork.Object,
            _mockLabelSwapper.Object,
            NullLogger<PendingWorkItemDrainService>.Instance,
            null, // IProjectStore
            _mockConsolidationDispatcher.Object,
            _mockConsolidationRunStore.Object);
    }

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

    private static async Task InvokeDrainAsync(PendingWorkItemDrainService service)
    {
        using var cts = new CancellationTokenSource();
        service.Signal();
        var task = service.StartAsync(cts.Token);
        await Task.Delay(3000);
        cts.Cancel();
        try { await task; } catch (OperationCanceledException) { }
        await service.StopAsync(CancellationToken.None);
    }

    public void Dispose()
    {
        using var db = new PipelineDbContext(_dbOptions);
        db.Database.EnsureDeleted();
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
/// </summary>
public sealed class PendingWorkItemDrainServiceConsolidationExceptionTests : IDisposable
{
    private readonly DbContextOptions<PipelineDbContext> _dbOptions;
    private readonly InMemoryDbContextFactory _dbFactory;
    private readonly Mock<ISignalRWorkDistributorAgentResolver> _mockResolver = new();
    private readonly Mock<IAgentCommunication> _mockAgentComm = new();
    private readonly Mock<ILabelSwapper> _mockLabelSwapper = new();
    private readonly Mock<IPendingWorkQuery> _mockPendingWork = new();
    private readonly Mock<IConsolidationDispatcher> _mockConsolidationDispatcher = new();
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
        _mockConsolidationDispatcher
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
        _mockConsolidationDispatcher
            .Setup(d => d.TryDispatchToAgentAsync(runId, ConsolidationRunType.BrainConsolidation, "template-1", "/tmp/ws", "agent-1", It.IsAny<CancellationToken>()))
            .Returns((string _, ConsolidationRunType _, string? _, string _, string _, CancellationToken _) =>
            {
                cts.Cancel(); // Simulate graceful shutdown — token is now cancelled
                throw new OperationCanceledException(cts.Token);
            });

        var service = new PendingWorkItemDrainService(
            _dbFactory,
            _mockResolver.Object,
            _mockAgentComm.Object,
            _runService,
            new WorkItemTransitionService(
                new CancellationAwareDbContextFactory(_dbOptions),
                NullLogger<WorkItemTransitionService>.Instance),
            _mockPendingWork.Object,
            _mockLabelSwapper.Object,
            NullLogger<PendingWorkItemDrainService>.Instance,
            null, // IProjectStore
            _mockConsolidationDispatcher.Object,
            _mockConsolidationRunStore.Object);

        // Act: start with the CTS that will be cancelled inside the mock
        service.Signal();
        var task = service.StartAsync(cts.Token);
        // TODO: Task.Delay is timing-dependent and may flake on slow CI runners.
        // Consider a synchronization signal (e.g., SemaphoreSlim) to confirm processing completed.
        await Task.Delay(3000);
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

    private PendingWorkItemDrainService CreateService()
    {
        return new PendingWorkItemDrainService(
            _dbFactory,
            _mockResolver.Object,
            _mockAgentComm.Object,
            _runService,
            _transitionService,
            _mockPendingWork.Object,
            _mockLabelSwapper.Object,
            NullLogger<PendingWorkItemDrainService>.Instance,
            null, // IProjectStore
            _mockConsolidationDispatcher.Object,
            _mockConsolidationRunStore.Object);
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

    private static async Task InvokeDrainAsync(PendingWorkItemDrainService service)
    {
        using var cts = new CancellationTokenSource();
        service.Signal();
        var task = service.StartAsync(cts.Token);
        await Task.Delay(3000);
        cts.Cancel();
        try { await task; } catch (OperationCanceledException) { }
        await service.StopAsync(CancellationToken.None);
    }

    public void Dispose()
    {
        using var db = new PipelineDbContext(_dbOptions);
        db.Database.EnsureDeleted();
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
