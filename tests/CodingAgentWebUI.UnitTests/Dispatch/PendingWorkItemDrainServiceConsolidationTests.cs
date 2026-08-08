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
/// <see cref="IConsolidationDispatchService.TryDispatchToAgentAsync"/> with token vending at drain time.
/// </summary>
public sealed class PendingWorkItemDrainServiceConsolidationTests : IDisposable
{
    private readonly DbContextOptions<PipelineDbContext> _dbOptions;
    private readonly InMemoryDbContextFactory _dbFactory;
    private readonly Mock<ISignalRWorkDistributorAgentResolver> _mockResolver = new();
    private readonly Mock<IAgentCommunication> _mockAgentComm = new();
    private readonly Mock<ILabelService> _mockLabelService = new();
    private readonly Mock<IPendingWorkQuery> _mockPendingWork = new();
    private readonly Mock<IConsolidationDispatchService> _mockConsolidationDispatchService = new();
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
        // TODO: Use explicit cast `(TemplateId?)"template-1"` instead of relying on implicit conversion for
        // the Moq setup and verify calls below, for consistency with the pattern used in
        // DrainPendingItems_ConsolidationItem_ShutdownDuringDispatch_RevertsWorkItemToPending and to avoid
        // silent breakage if Moq changes how it resolves implicit conversions in argument matching.
        _mockConsolidationDispatchService
            .Setup(d => d.TryDispatchToAgentAsync(runId, ConsolidationRunType.BrainConsolidation, "template-1", "/tmp/ws", "agent-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var service = CreateService();

        // Act
        await InvokeDrainAsync(service);

        // Assert: TryDispatchToAgentAsync was called (token vending occurs within)
        _mockConsolidationDispatchService.Verify(
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
        _mockConsolidationDispatchService
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
        item.RetryCount.Should().Be(0, "consolidation dispatch failures must NOT increment RetryCount");
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

        _mockConsolidationDispatchService.Verify(
            d => d.TryDispatchToAgentAsync(It.IsAny<string>(), It.IsAny<ConsolidationRunType>(), It.IsAny<TemplateId?>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
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
            MakeDeps());

        // Act
        await InvokeDrainAsync(service);

        // Assert: item remains Pending (not dispatched)
        await using var db = await _dbFactory.CreateDbContextAsync();
        var item = await db.WorkItems.FindAsync(workItemId);
        item!.Status.Should().Be(WorkItemStatus.Pending);

        // Dispatch was never attempted
        _mockConsolidationDispatchService.Verify(
            d => d.TryDispatchToAgentAsync(It.IsAny<string>(), It.IsAny<ConsolidationRunType>(), It.IsAny<TemplateId?>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
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
        _mockConsolidationDispatchService.Verify(
            d => d.TryDispatchToAgentAsync(It.IsAny<string>(), It.IsAny<ConsolidationRunType>(), It.IsAny<TemplateId?>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    private PendingWorkItemDrainService CreateService()
    {
        return new PendingWorkItemDrainService(
            MakeDeps(),
            null, // IProjectStore
            _mockConsolidationDispatchService.Object,
            _mockConsolidationRunStore.Object);
    }

    private DrainServiceDependencies MakeDeps() =>
        new(_dbFactory, _mockResolver.Object, _mockAgentComm.Object,
            _runService, _transitionService, _mockPendingWork.Object,
            _mockLabelService.Object, NullLogger<PendingWorkItemDrainService>.Instance);

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
/// </summary>
public sealed class PendingWorkItemDrainServiceConsolidationExceptionTests : IDisposable
{
    private readonly DbContextOptions<PipelineDbContext> _dbOptions;
    private readonly InMemoryDbContextFactory _dbFactory;
    private readonly Mock<ISignalRWorkDistributorAgentResolver> _mockResolver = new();
    private readonly Mock<IAgentCommunication> _mockAgentComm = new();
    private readonly Mock<ILabelService> _mockLabelService = new();
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
            .Setup(d => d.TryDispatchToAgentAsync(runId, ConsolidationRunType.BrainConsolidation, (TemplateId?)"template-1", "/tmp/ws", "agent-1", It.IsAny<CancellationToken>()))
            .Returns((string _, ConsolidationRunType _, TemplateId? _, string _, string _, CancellationToken _) =>
            {
                cts.Cancel(); // Simulate graceful shutdown — token is now cancelled
                throw new OperationCanceledException(cts.Token);
            });

        var service = new PendingWorkItemDrainService(
            new DrainServiceDependencies(
                _dbFactory,
                _mockResolver.Object,
                _mockAgentComm.Object,
                _runService,
                new WorkItemTransitionService(
                    new CancellationAwareDbContextFactory(_dbOptions),
                    NullLogger<WorkItemTransitionService>.Instance),
                _mockPendingWork.Object,
                _mockLabelService.Object,
                NullLogger<PendingWorkItemDrainService>.Instance),
            null, // IProjectStore
            _mockConsolidationDispatchService.Object,
            _mockConsolidationRunStore.Object);

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

    private PendingWorkItemDrainService CreateService()
    {
        return new PendingWorkItemDrainService(
            MakeDeps(),
            null, // IProjectStore
            _mockConsolidationDispatchService.Object,
            _mockConsolidationRunStore.Object);
    }

    private DrainServiceDependencies MakeDeps() =>
        new(_dbFactory, _mockResolver.Object, _mockAgentComm.Object,
            _runService, _transitionService, _mockPendingWork.Object,
            _mockLabelService.Object, NullLogger<PendingWorkItemDrainService>.Instance);

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
/// Direct-invocation tests for the extracted <c>DispatchConsolidationItemAsync</c> private method,
/// satisfying the acceptance criterion: "consolidation and pipeline dispatch paths are separate methods
/// independently callable from tests." Uses reflection consistent with the existing InvokeDrainAsync pattern.
/// </summary>
public sealed class DispatchConsolidationItemAsyncTests : IDisposable
{
    private readonly DbContextOptions<PipelineDbContext> _dbOptions;
    private readonly InMemoryDbContextFactory _dbFactory;
    private readonly Mock<ISignalRWorkDistributorAgentResolver> _mockResolver = new();
    private readonly Mock<IAgentCommunication> _mockAgentComm = new();
    private readonly Mock<ILabelService> _mockLabelService = new();
    private readonly Mock<IPendingWorkQuery> _mockPendingWork = new();
    private readonly Mock<IConsolidationDispatchService> _mockConsolidationDispatchService = new();
    private readonly Mock<IConsolidationRunStore> _mockConsolidationRunStore = new();
    private readonly OrchestratorRunService _runService;
    private readonly WorkItemTransitionService _transitionService;

    public DispatchConsolidationItemAsyncTests()
    {
        _dbOptions = new DbContextOptionsBuilder<PipelineDbContext>()
            .UseInMemoryDatabase($"DispatchConsolidationItemTest_{Guid.NewGuid()}")
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        _dbFactory = new InMemoryDbContextFactory(_dbOptions);
        _runService = new OrchestratorRunService(Serilog.Log.Logger);
        _transitionService = new WorkItemTransitionService(_dbFactory, NullLogger<WorkItemTransitionService>.Instance);
        _mockPendingWork.Setup(p => p.GetPendingJobsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<PendingJob>().AsReadOnly());
    }

    [Fact]
    public async Task DispatchConsolidationItem_SuccessfulDispatch_ReturnsTrue_AndAssignsJob()
    {
        // Arrange
        var runId = Guid.NewGuid().ToString();
        var workItemId = Guid.Parse(runId);
        // TODO: workItemId == Guid.Parse(runId) ties the DB item ID to the runId string. If production code
        // ever derives runId from a field other than request.IssueIdentifier, the TryDispatchToAgentAsync mock
        // will silently not match (returns false by default), and result.Should().BeTrue() will fail — which is
        // detectable. No silent-pass risk here, but the coupling is worth noting for future refactors.
        var (item, request) = await InsertAndBuildItem(workItemId, runId, ConsolidationRunType.BrainConsolidation, "tpl-1", "/tmp/ws", "agent-1");

        _mockConsolidationRunStore.Setup(s => s.GetByIdAsync(runId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ConsolidationRun { RunId = runId, Status = ConsolidationRunStatus.Queued, Type = ConsolidationRunType.BrainConsolidation, StartedAtUtc = DateTime.UtcNow });
        _mockConsolidationDispatchService
            .Setup(d => d.TryDispatchToAgentAsync(runId, ConsolidationRunType.BrainConsolidation, "tpl-1", "/tmp/ws", "agent-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        _mockResolver.Setup(r => r.AssignJob("agent-1", workItemId.ToString()));

        var service = CreateService();

        // Act
        var result = await InvokeDispatchConsolidationItemAsync(service, item, request, "agent-1", CancellationToken.None);

        // Assert
        result.Should().BeTrue("successful dispatch must return true");
        _mockResolver.Verify(r => r.AssignJob("agent-1", workItemId.ToString()), Times.Once);
        await using var db = await _dbFactory.CreateDbContextAsync();
        var stored = await db.WorkItems.FindAsync(workItemId);
        stored!.Status.Should().Be(WorkItemStatus.Dispatched);
        stored.AssignedAgentId.Should().Be("agent-1");
    }

    [Fact]
    public async Task DispatchConsolidationItem_DispatchReturnsFalse_RevertsToPending_RetryCountUnchanged()
    {
        // Verifies Block A behavior: false return uses ct, no RetryCount increment
        var runId = Guid.NewGuid().ToString();
        var workItemId = Guid.Parse(runId);
        var (item, request) = await InsertAndBuildItem(workItemId, runId, ConsolidationRunType.BrainConsolidation, null, "/tmp/ws", "agent-1");

        _mockConsolidationRunStore.Setup(s => s.GetByIdAsync(runId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ConsolidationRun { RunId = runId, Status = ConsolidationRunStatus.Queued, Type = ConsolidationRunType.BrainConsolidation, StartedAtUtc = DateTime.UtcNow });
        // TODO: The mock below uses a null TemplateId? argument. If TryDispatchToAgentAsync is never called
        // (e.g., a short-circuit in the cancelled-run guard), the mock returns false by default — the same
        // value the test expects — so the test would pass vacuously. Add
        // _mockConsolidationDispatchService.Verify(d => d.TryDispatchToAgentAsync(...), Times.Once)
        // to close this gap and confirm the dispatch path was actually exercised.
        _mockConsolidationDispatchService
            .Setup(d => d.TryDispatchToAgentAsync(runId, ConsolidationRunType.BrainConsolidation, null, "/tmp/ws", "agent-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);
        _mockResolver.Setup(r => r.ReleaseAgent("agent-1"));

        var service = CreateService();

        // Act
        var result = await InvokeDispatchConsolidationItemAsync(service, item, request, "agent-1", CancellationToken.None);

        // Assert
        result.Should().BeFalse("failed dispatch must return false");
        await using var db = await _dbFactory.CreateDbContextAsync();
        var stored = await db.WorkItems.FindAsync(workItemId);
        stored!.Status.Should().Be(WorkItemStatus.Pending);
        stored.AssignedAgentId.Should().BeNull();
        stored.DispatchedAt.Should().BeNull();
        stored.RetryCount.Should().Be(0, "consolidation false-path must NOT increment RetryCount");
        _mockResolver.Verify(r => r.ReleaseAgent("agent-1"), Times.Once);
    }

    [Fact]
    public async Task DispatchConsolidationItem_DispatchThrows_RevertsToPending_RetryCountUnchanged()
    {
        // Verifies Block B behavior: exception uses CancellationToken.None via TryRevertToPendingAsync, no RetryCount
        var runId = Guid.NewGuid().ToString();
        var workItemId = Guid.Parse(runId);
        var (item, request) = await InsertAndBuildItem(workItemId, runId, ConsolidationRunType.BrainConsolidation, "tpl-1", "/tmp/ws", "agent-1");

        _mockConsolidationRunStore.Setup(s => s.GetByIdAsync(runId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ConsolidationRun { RunId = runId, Status = ConsolidationRunStatus.Queued, Type = ConsolidationRunType.BrainConsolidation, StartedAtUtc = DateTime.UtcNow });
        _mockConsolidationDispatchService
            .Setup(d => d.TryDispatchToAgentAsync(It.IsAny<string>(), It.IsAny<ConsolidationRunType>(), It.IsAny<TemplateId?>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Token vending failed"));
        _mockResolver.Setup(r => r.ReleaseAgent("agent-1"));

        var service = CreateService();

        // Act
        var result = await InvokeDispatchConsolidationItemAsync(service, item, request, "agent-1", CancellationToken.None);

        // Assert
        result.Should().BeFalse("exception during dispatch must return false");
        await using var db = await _dbFactory.CreateDbContextAsync();
        var stored = await db.WorkItems.FindAsync(workItemId);
        stored!.Status.Should().Be(WorkItemStatus.Pending);
        stored.AssignedAgentId.Should().BeNull();
        // TODO: stored.DispatchedAt.Should().BeNull() is the final state, which is consistent with both
        // "item was never moved out of Pending" and "item was Dispatched then reverted". The throw is set up
        // on TryDispatchToAgentAsync which is called after TransitionAsync(Dispatched) in the production code,
        // so the item will have been transiently Dispatched. Consider capturing DispatchedAt before the Act
        // call and asserting it was non-null at some point (e.g., via a transition history or an intermediate
        // DB read), to distinguish these two cases and make the exception-path coverage unambiguous.
        stored.DispatchedAt.Should().BeNull();
        stored.RetryCount.Should().Be(0, "consolidation exception-path must NOT increment RetryCount");
        _mockResolver.Verify(r => r.ReleaseAgent("agent-1"), Times.Once);
    }

    [Fact]
    public async Task DispatchConsolidationItem_CancelledRun_TransitionsToCancelled_ReturnsFalse()
    {
        var runId = Guid.NewGuid().ToString();
        var workItemId = Guid.Parse(runId);
        var (item, request) = await InsertAndBuildItem(workItemId, runId, ConsolidationRunType.BrainConsolidation, null, "/tmp/ws", "agent-1");

        _mockConsolidationRunStore.Setup(s => s.GetByIdAsync(runId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ConsolidationRun { RunId = runId, Status = ConsolidationRunStatus.Cancelled, Type = ConsolidationRunType.BrainConsolidation, StartedAtUtc = DateTime.UtcNow });
        _mockResolver.Setup(r => r.ReleaseAgent("agent-1"));

        var service = CreateService();

        // Act
        var result = await InvokeDispatchConsolidationItemAsync(service, item, request, "agent-1", CancellationToken.None);

        // Assert
        result.Should().BeFalse("cancelled run must return false");
        await using var db = await _dbFactory.CreateDbContextAsync();
        var stored = await db.WorkItems.FindAsync(workItemId);
        stored!.Status.Should().Be(WorkItemStatus.Cancelled);
        stored.CompletedAt.Should().NotBeNull();
        _mockConsolidationDispatchService.Verify(
            d => d.TryDispatchToAgentAsync(It.IsAny<string>(), It.IsAny<ConsolidationRunType>(), It.IsAny<TemplateId?>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task DispatchConsolidationItem_DispatchReturnsFalse_UsesCallerCancellationToken()
    {
        // Characterization test: verifies that when dispatch returns false, the revert path
        // forwards the caller's ct to TryRevertToPendingAsync rather than ignoring it.
        //
        // Strategy: cancel the CTS inside the TryDispatchToAgentAsync mock callback. At that point
        // the initial TransitionAsync(Dispatched) has already completed successfully, so the item
        // is in Dispatched state. The false-return path then calls TryRevertToPendingAsync(ct) with
        // the now-cancelled token, which causes CancellationAwareDbContextFactory to throw
        // OperationCanceledException. TryRevertToPendingAsync swallows the exception, leaving the
        // item Dispatched. If ct were not forwarded (CancellationToken.None used instead), the revert
        // transition would succeed and the item would be Pending — failing this assertion.
        var runId = Guid.NewGuid().ToString();
        var workItemId = Guid.Parse(runId);

        // Insert item using the standard factory so it exists in the DB
        var (item, request) = await InsertAndBuildItem(workItemId, runId, ConsolidationRunType.BrainConsolidation, null, "/tmp/ws", "agent-1");

        using var cts = new CancellationTokenSource();

        _mockConsolidationRunStore.Setup(s => s.GetByIdAsync(runId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ConsolidationRun { RunId = runId, Status = ConsolidationRunStatus.Queued, Type = ConsolidationRunType.BrainConsolidation, StartedAtUtc = DateTime.UtcNow });
        _mockConsolidationDispatchService
            .Setup(d => d.TryDispatchToAgentAsync(runId, ConsolidationRunType.BrainConsolidation, null, "/tmp/ws", "agent-1", It.IsAny<CancellationToken>()))
            .Returns((string _, ConsolidationRunType _, TemplateId? _, string _, string _, CancellationToken _) =>
            {
                cts.Cancel(); // cancel AFTER the initial Dispatched transition succeeded
                return Task.FromResult(false);
            });
        _mockResolver.Setup(r => r.ReleaseAgent("agent-1"));

        // Use a CancellationAwareDbContextFactory so that when the cancelled token reaches
        // TryRevertToPendingAsync, the revert transition fails
        var cancellationAwareFactory = new CancellationAwareDbContextFactory(_dbOptions);
        var cancellingTransitionService = new WorkItemTransitionService(cancellationAwareFactory, NullLogger<WorkItemTransitionService>.Instance);
        var service = new PendingWorkItemDrainService(
            new DrainServiceDependencies(
                _dbFactory, _mockResolver.Object, _mockAgentComm.Object,
                _runService, cancellingTransitionService, _mockPendingWork.Object,
                _mockLabelService.Object, NullLogger<PendingWorkItemDrainService>.Instance),
            null,
            _mockConsolidationDispatchService.Object,
            _mockConsolidationRunStore.Object);

        // Act: pass cts.Token — still live when the method starts, cancelled inside the mock callback
        var result = await InvokeDispatchConsolidationItemAsync(service, item, request, "agent-1", cts.Token);

        // Assert: dispatch returned false, method returns false
        result.Should().BeFalse("failed dispatch must return false");
        _mockConsolidationDispatchService.Verify(
            d => d.TryDispatchToAgentAsync(runId, ConsolidationRunType.BrainConsolidation, null, "/tmp/ws", "agent-1", It.IsAny<CancellationToken>()),
            Times.Once,
            "TryDispatchToAgentAsync must be called — confirming the dispatch path was exercised");

        // The revert was cancelled via the forwarded ct → item remains Dispatched
        // (TryRevertToPendingAsync swallowed the OperationCanceledException from the cancelled revert)
        // If ct were ignored and CancellationToken.None used instead, the revert would succeed → Pending
        await using var db = await _dbFactory.CreateDbContextAsync();
        var stored = await db.WorkItems.FindAsync(workItemId);
        stored!.Status.Should().Be(WorkItemStatus.Dispatched,
            "revert was cancelled via the caller's ct — item must remain Dispatched, confirming ct was forwarded");
    }

    private PendingWorkItemDrainService CreateService() =>
        new(new DrainServiceDependencies(
                _dbFactory, _mockResolver.Object, _mockAgentComm.Object,
                _runService, _transitionService, _mockPendingWork.Object,
                _mockLabelService.Object, NullLogger<PendingWorkItemDrainService>.Instance),
            null,
            _mockConsolidationDispatchService.Object,
            _mockConsolidationRunStore.Object);

    private async Task<(WorkItemEntity item, JobDistributionRequest request)> InsertAndBuildItem(
        Guid workItemId, string runId, ConsolidationRunType runType, string? templateId, string workspacePath, string agentSelector)
    {
        var request = new JobDistributionRequest
        {
            IssueIdentifier = runId,
            IssueProviderConfigId = "consolidation",
            RepoProviderConfigId = "",
            InitiatedBy = "consolidation",
            TaskType = WorkItemTaskType.Consolidation,
            AgentSelector = agentSelector,
            TimeoutSeconds = 0,
            ConsolidationRunType = runType,
            ConsolidationTemplateId = templateId,
            ConsolidationWorkspacePath = workspacePath,
            RunId = runId
        };
        var payload = JsonSerializer.Serialize(request, PipelineJsonOptions.Default);

        var entity = new WorkItemEntity
        {
            Id = workItemId,
            TaskType = WorkItemTaskType.Consolidation,
            IssueIdentifier = runId,
            IssueProviderConfigId = "consolidation",
            Status = WorkItemStatus.Pending,
            Payload = payload,
            AgentSelector = agentSelector,
            CreatedAt = DateTimeOffset.UtcNow,
            TimeoutSeconds = 0
        };
        await using var db = await _dbFactory.CreateDbContextAsync();
        db.WorkItems.Add(entity);
        await db.SaveChangesAsync();

        return (entity, request);
    }

    private static async Task<bool> InvokeDispatchConsolidationItemAsync(
        PendingWorkItemDrainService service, WorkItemEntity item, JobDistributionRequest request,
        string agentId, CancellationToken ct)
    {
        var method = typeof(PendingWorkItemDrainService).GetMethod("DispatchConsolidationItemAsync",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
            ?? throw new InvalidOperationException("DispatchConsolidationItemAsync not found");
        var task = (Task<bool>)method.Invoke(service, [item, request, (AgentId)agentId, ct])!;
        return await task;
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
    /// Factory that throws <see cref="OperationCanceledException"/> when <see cref="CreateDbContextAsync"/>
    /// is called with a cancelled token — simulating real DB provider behavior.
    /// Used to verify that the caller's <c>ct</c> is forwarded to <c>TryRevertToPendingAsync</c>
    /// in the false-return path of <c>DispatchConsolidationItemAsync</c>.
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
/// Direct-invocation tests for the extracted <c>TryRevertToPendingAsync</c> private method.
/// Verifies that the <paramref name="incrementRetryCount"/> parameter correctly controls RetryCount
/// and that exceptions from the transition are swallowed (stuck-item detector fallback).
/// </summary>
public sealed class TryRevertToPendingAsyncTests : IDisposable
{
    private readonly DbContextOptions<PipelineDbContext> _dbOptions;
    private readonly InMemoryDbContextFactory _dbFactory;
    private readonly Mock<ISignalRWorkDistributorAgentResolver> _mockResolver = new();
    private readonly Mock<IAgentCommunication> _mockAgentComm = new();
    private readonly Mock<ILabelService> _mockLabelService = new();
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
        var service = CreateService();

        // Act
        await InvokeTryRevertToPendingAsync(service, workItemId, incrementRetryCount: false);

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
        var service = CreateService();

        // Act
        await InvokeTryRevertToPendingAsync(service, workItemId, incrementRetryCount: true);

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
        var service = CreateServiceWithTransition(failingTransition);

        // Act: must not throw
        var act = async () => await InvokeTryRevertToPendingAsync(service, workItemId, incrementRetryCount: true);

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
        var service = CreateServiceWithTransition(transitionService);

        // Negative case: a cancelled token causes the revert to fail → item stays Dispatched.
        // Uses the shared InvokeTryRevertToPendingAsync helper so that signature changes are caught at compile time.
        var act = async () => await InvokeTryRevertToPendingAsync(service, workItemId, false, cts.Token);
        await act.Should().NotThrowAsync("TryRevertToPendingAsync must swallow the OperationCanceledException from a cancelled token");

        await using var db = await _dbFactory.CreateDbContextAsync();
        var item = await db.WorkItems.FindAsync(workItemId);
        item!.Status.Should().Be(WorkItemStatus.Dispatched,
            "revert was cancelled via the provided ct — item must remain Dispatched (stuck-item detector will handle)");

        // Positive case: an uncancelled token allows the revert to succeed → item transitions to Pending.
        // This distinguishes "ct was forwarded and respected" from "transition always fails regardless of ct".
        await InvokeTryRevertToPendingAsync(service, workItemId, false, CancellationToken.None);

        await using var db2 = await _dbFactory.CreateDbContextAsync();
        var itemAfter = await db2.WorkItems.FindAsync(workItemId);
        itemAfter!.Status.Should().Be(WorkItemStatus.Pending,
            "an uncancelled token must allow the revert to succeed — confirming ct forwarding is the cause of the difference");
    }

    private PendingWorkItemDrainService CreateService()
    {
        var transitionService = new WorkItemTransitionService(_dbFactory, NullLogger<WorkItemTransitionService>.Instance);
        return CreateServiceWithTransition(transitionService);
    }

    private PendingWorkItemDrainService CreateServiceWithTransition(WorkItemTransitionService transitionService) =>
        new(new DrainServiceDependencies(
            _dbFactory, _mockResolver.Object, _mockAgentComm.Object,
            _runService, transitionService, _mockPendingWork.Object,
            _mockLabelService.Object, NullLogger<PendingWorkItemDrainService>.Instance));

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

    private static async Task InvokeTryRevertToPendingAsync(
        PendingWorkItemDrainService service, Guid workItemId, bool incrementRetryCount,
        CancellationToken ct = default)
    {
        var method = typeof(PendingWorkItemDrainService).GetMethod("TryRevertToPendingAsync",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
            ?? throw new InvalidOperationException("TryRevertToPendingAsync not found");
        var task = (Task)method.Invoke(service, [workItemId, incrementRetryCount, ct])!;
        await task;
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
    /// A <see cref="IDbContextFactory{PipelineDbContext}"/> that always throws <see cref="InvalidOperationException"/>.
    /// Used to force <see cref="WorkItemTransitionService.TransitionAsync"/> to throw so that
    /// <c>TryRevertToPendingAsync</c> exception-swallowing can be verified.
    /// </summary>
    private sealed class AlwaysThrowingDbContextFactory : IDbContextFactory<PipelineDbContext>
    {
        public PipelineDbContext CreateDbContext() => throw new InvalidOperationException("Simulated DB failure");
        public Task<PipelineDbContext> CreateDbContextAsync(CancellationToken ct = default)
            => throw new InvalidOperationException("Simulated DB failure");
    }

    /// <summary>
    /// A <see cref="IDbContextFactory{PipelineDbContext}"/> that throws <see cref="OperationCanceledException"/>
    /// when <see cref="CreateDbContextAsync"/> is called with a cancelled token.
    /// Simulates real DB provider behavior (the EF Core InMemory provider ignores cancellation).
    /// Used to verify that the optional <c>ct</c> parameter is forwarded to <see cref="WorkItemTransitionService.TransitionAsync"/>.
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
