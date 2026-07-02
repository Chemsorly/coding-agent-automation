using AwesomeAssertions;
using CodingAgentWebUI.Infrastructure.Persistence;
using CodingAgentWebUI.Infrastructure.Persistence.Entities;
using CodingAgentWebUI.Infrastructure.Persistence.Services;
using CodingAgentWebUI.Orchestration;
using CodingAgentWebUI.Orchestration.Dispatch;
using CodingAgentWebUI.Orchestration.Registry;
using CodingAgentWebUI.Pipeline;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Pipeline.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using ILogger = Serilog.ILogger;

namespace CodingAgentWebUI.IntegrationTests.DbMode;

/// <summary>
/// Integration tests for RunLifecycleManager with real services (InMemory EF + real
/// OrchestratorRunService + real AgentRegistryService + mock ILabelSwapper).
/// Validates full lifecycle behavior with real WorkItemTransitionService + real DB (InMemory EF),
/// proving mode parity between in-memory and DB paths.
/// </summary>
public sealed class RunLifecycleIntegrationTests : IDisposable
{
    private readonly DbContextOptions<PipelineDbContext> _dbOptions;
    private readonly InMemoryDbContextFactory _dbFactory;
    private readonly WorkItemTransitionService _transitionService;
    private readonly OrchestratorRunService _runService;
    private readonly AgentRegistryService _registry;
    private readonly JobDispatcherService _dispatcher;
    private readonly Mock<IPipelineRunHistoryService> _mockHistoryService;
    private readonly Mock<ILabelSwapper> _mockLabelSwapper;
    private readonly Mock<ILogger> _mockLogger;
    private readonly RunLifecycleManager _lifecycleManager;

    public RunLifecycleIntegrationTests()
    {
        _dbOptions = new DbContextOptionsBuilder<PipelineDbContext>()
            .UseInMemoryDatabase(databaseName: $"RunLifecycleIntegration-{Guid.NewGuid()}")
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;

        using (var ctx = new InMemoryPipelineDbContext(_dbOptions))
        {
            ctx.Database.EnsureCreated();
        }

        _dbFactory = new InMemoryDbContextFactory(_dbOptions);
        _transitionService = new WorkItemTransitionService(_dbFactory, NullLogger<WorkItemTransitionService>.Instance);
        _mockLogger = new Mock<ILogger>();
        _runService = new OrchestratorRunService(_mockLogger.Object);
        _registry = new AgentRegistryService(_mockLogger.Object);
        _dispatcher = new JobDispatcherService(_registry, _mockLogger.Object);
        _mockHistoryService = new Mock<IPipelineRunHistoryService>();
        _mockLabelSwapper = new Mock<ILabelSwapper>();

        _lifecycleManager = new RunLifecycleManager(
            _runService,
            _mockHistoryService.Object,
            _registry,
            _mockLabelSwapper.Object,
            _dispatcher,
            _mockLogger.Object,
            _transitionService);
    }

    public void Dispose()
    {
        using var db = new InMemoryPipelineDbContext(_dbOptions);
        db.Database.EnsureDeleted();
    }

    #region Test 1: HeartbeatMonitor_FailsWorkItem_WhenAgentDisconnects

    [Fact]
    public async Task HeartbeatMonitor_FailsWorkItem_WhenAgentDisconnects()
    {
        // Arrange: Insert a WorkItem in Dispatched status
        var runId = Guid.NewGuid();
        await using (var db = await _dbFactory.CreateDbContextAsync())
        {
            db.WorkItems.Add(new WorkItemEntity
            {
                Id = runId,
                IssueIdentifier = "owner/repo#100",
                IssueProviderConfigId = "ip-1",
                Status = WorkItemStatus.Dispatched,
                CreatedAt = DateTimeOffset.UtcNow,
                TaskType = WorkItemTaskType.Implementation
            });
            await db.SaveChangesAsync();
        }

        // Create a PipelineRun in OrchestratorRunService
        var pipelineRun = new PipelineRun
        {
            RunId = runId.ToString(),
            IssueIdentifier = "owner/repo#100",
            IssueTitle = "Test Issue",
            IssueProviderConfigId = "ip-1",
            RepoProviderConfigId = "rp-1",
            StartedAt = DateTime.UtcNow,
            AgentId = "agent-disconnect"
        };
        _runService.AddRun(pipelineRun);

        // Register agent as connected then disconnect
        var agent = _registry.Register(new AgentRegistrationMessage
        {
            AgentId = "agent-disconnect",
            Hostname = "host-1",
            Labels = new[] { "dotnet" }
        }, "conn-1");
        agent.ActiveJobId = runId.ToString();
        _registry.TransitionStatus("agent-disconnect", AgentStatus.Busy);

        // Act: Call FailRunAsync (same path HeartbeatMonitor takes for disconnected agents)
        var result = await _lifecycleManager.FailRunAsync(runId.ToString(), "Agent disconnected", CancellationToken.None);

        // Assert
        result.Should().NotBeNull();
        result!.RunId.Should().Be(runId.ToString());

        // WorkItem transitioned to Failed with CompletedAt set
        await using (var db = await _dbFactory.CreateDbContextAsync())
        {
            var item = await db.WorkItems.FindAsync(runId);
            item!.Status.Should().Be(WorkItemStatus.Failed);
            item.CompletedAt.Should().NotBeNull();
        }

        // PipelineRun removed from active
        _runService.GetRun(runId.ToString()).Should().BeNull();

        // Agent deregistered (returned to Idle with cleared state)
        agent.Status.Should().Be(AgentStatus.Idle);
        agent.ActiveJobId.Should().BeNull();
    }

    #endregion

    #region Test 2: DrainService_RestartRecovery_CreatesRunAndDeliversToAgent

    [Fact]
    public async Task CompleteRunAsync_AfterDrainDispatch_TransitionsWorkItemToSucceeded()
    {
        // This test simulates the drain → complete lifecycle:
        // A WorkItem in Dispatched status gets picked up, transitions to Running, then completes.
        var runId = Guid.NewGuid();

        // Setup: Insert a WorkItem in Dispatched status (simulating post-dispatch state)
        await using (var db = await _dbFactory.CreateDbContextAsync())
        {
            db.WorkItems.Add(new WorkItemEntity
            {
                Id = runId,
                IssueIdentifier = "owner/repo#200",
                IssueProviderConfigId = "ip-2",
                Status = WorkItemStatus.Dispatched,
                CreatedAt = DateTimeOffset.UtcNow,
                TaskType = WorkItemTaskType.Implementation
            });
            await db.SaveChangesAsync();
        }

        // Create PipelineRun (simulating what CreateDispatchedRunAsync does)
        var pipelineRun = new PipelineRun
        {
            RunId = runId.ToString(),
            IssueIdentifier = "owner/repo#200",
            IssueTitle = "Drain Test",
            IssueProviderConfigId = "ip-2",
            RepoProviderConfigId = "rp-2",
            StartedAt = DateTime.UtcNow,
            AgentId = "agent-drain"
        };
        _runService.AddRun(pipelineRun);

        // Transition WorkItem to Running (simulating agent accepting)
        await _transitionService.TransitionAsync(runId, WorkItemStatus.Running, ct: CancellationToken.None);

        // Act: Complete the run via lifecycle manager
        var result = await _lifecycleManager.CompleteRunAsync(runId.ToString(), WorkItemStatus.Succeeded, CancellationToken.None);

        // Assert
        result.Should().NotBeNull();

        // WorkItem in DB should be Succeeded with CompletedAt
        await using (var db = await _dbFactory.CreateDbContextAsync())
        {
            var item = await db.WorkItems.FindAsync(runId);
            item!.Status.Should().Be(WorkItemStatus.Succeeded);
            item.CompletedAt.Should().NotBeNull();
        }

        // PipelineRun removed from in-memory service
        _runService.GetRun(runId.ToString()).Should().BeNull();

        // History persisted
        _mockHistoryService.Verify(h => h.AddRunToHistoryAsync(
            It.Is<PipelineRun>(r => r.RunId == runId.ToString()),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    #endregion

    #region Test 3: Cancel_TransitionsWorkItemAndClearsInMemory

    [Fact]
    public async Task CancelRunAsync_TransitionsWorkItemAndClearsInMemory()
    {
        // Arrange: Insert a WorkItem in Running status
        var runId = Guid.NewGuid();
        await using (var db = await _dbFactory.CreateDbContextAsync())
        {
            db.WorkItems.Add(new WorkItemEntity
            {
                Id = runId,
                IssueIdentifier = "owner/repo#300",
                IssueProviderConfigId = "ip-3",
                Status = WorkItemStatus.Running,
                CreatedAt = DateTimeOffset.UtcNow,
                TaskType = WorkItemTaskType.Implementation
            });
            await db.SaveChangesAsync();
        }

        // Create a PipelineRun
        var pipelineRun = new PipelineRun
        {
            RunId = runId.ToString(),
            IssueIdentifier = "owner/repo#300",
            IssueTitle = "Cancel Test",
            IssueProviderConfigId = "ip-3",
            RepoProviderConfigId = "rp-3",
            StartedAt = DateTime.UtcNow,
            AgentId = "agent-cancel"
        };
        _runService.AddRun(pipelineRun);

        // Register a Busy agent with ActiveJobId
        var agent = _registry.Register(new AgentRegistrationMessage
        {
            AgentId = "agent-cancel",
            Hostname = "host-3",
            Labels = new[] { "dotnet" }
        }, "conn-3");
        agent.ActiveJobId = runId.ToString();
        _registry.TransitionStatus("agent-cancel", AgentStatus.Busy);

        // Act
        var result = await _lifecycleManager.CancelRunAsync(runId.ToString(), CancellationToken.None);

        // Assert
        result.Should().NotBeNull();
        result!.RunId.Should().Be(runId.ToString());

        // WorkItem transitioned to Cancelled
        await using (var db = await _dbFactory.CreateDbContextAsync())
        {
            var item = await db.WorkItems.FindAsync(runId);
            item!.Status.Should().Be(WorkItemStatus.Cancelled);
            item.CompletedAt.Should().NotBeNull();
        }

        // PipelineRun removed from in-memory
        _runService.GetRun(runId.ToString()).Should().BeNull();

        // Agent Idle with null ActiveJobId
        agent.Status.Should().Be(AgentStatus.Idle);
        agent.ActiveJobId.Should().BeNull();

        // Label swapped to cancelled
        _mockLabelSwapper.Verify(l => l.SwapLabelAsync(
            "ip-3", "owner/repo#300", AgentLabels.Cancelled,
            LabelTargetKind.Issue, It.IsAny<CancellationToken>()), Times.Once);
    }

    #endregion

    #region Test 4: CompleteRunAsync_TransitionsWorkItemToSucceeded

    [Fact]
    public async Task CompleteRunAsync_TransitionsWorkItemToSucceeded()
    {
        // Arrange: Insert a WorkItem in Running status
        var runId = Guid.NewGuid();
        await using (var db = await _dbFactory.CreateDbContextAsync())
        {
            db.WorkItems.Add(new WorkItemEntity
            {
                Id = runId,
                IssueIdentifier = "owner/repo#400",
                IssueProviderConfigId = "ip-4",
                Status = WorkItemStatus.Running,
                CreatedAt = DateTimeOffset.UtcNow,
                TaskType = WorkItemTaskType.Implementation
            });
            await db.SaveChangesAsync();
        }

        // Create a PipelineRun
        var pipelineRun = new PipelineRun
        {
            RunId = runId.ToString(),
            IssueIdentifier = "owner/repo#400",
            IssueTitle = "Complete Test",
            IssueProviderConfigId = "ip-4",
            RepoProviderConfigId = "rp-4",
            StartedAt = DateTime.UtcNow,
            AgentId = "agent-complete"
        };
        _runService.AddRun(pipelineRun);

        // Act
        var result = await _lifecycleManager.CompleteRunAsync(runId.ToString(), WorkItemStatus.Succeeded, CancellationToken.None);

        // Assert
        result.Should().NotBeNull();
        result!.RunId.Should().Be(runId.ToString());

        // WorkItem in DB
        await using (var db = await _dbFactory.CreateDbContextAsync())
        {
            var item = await db.WorkItems.FindAsync(runId);
            item!.Status.Should().Be(WorkItemStatus.Succeeded);
            item.CompletedAt.Should().NotBeNull();
        }

        // PipelineRun removed
        _runService.GetRun(runId.ToString()).Should().BeNull();

        // History persisted
        _mockHistoryService.Verify(h => h.AddRunToHistoryAsync(
            It.Is<PipelineRun>(r => r.RunId == runId.ToString()),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    #endregion

    #region Test 5: AgentAcceptedRunAsync_SwapsLabelAndSetsAgentState

    [Fact]
    public async Task AgentAcceptedRunAsync_SwapsLabelAndSetsAgentState()
    {
        // Arrange: Create a PipelineRun with AgentId=null
        var runId = Guid.NewGuid().ToString();
        var pipelineRun = new PipelineRun
        {
            RunId = runId,
            IssueIdentifier = "owner/repo#500",
            IssueTitle = "Accept Test",
            IssueProviderConfigId = "ip-5",
            RepoProviderConfigId = "rp-5",
            StartedAt = DateTime.UtcNow,
            AgentId = null
        };
        _runService.AddRun(pipelineRun);

        // Register an Idle agent
        var agent = _registry.Register(new AgentRegistrationMessage
        {
            AgentId = "agent-accept",
            Hostname = "host-5",
            Labels = new[] { "dotnet" }
        }, "conn-5");

        // Act
        await _lifecycleManager.AgentAcceptedRunAsync(
            runId, "agent-accept",
            "owner/repo#500", "ip-5", "rp-5",
            PipelineRunType.Implementation, CancellationToken.None);

        // Assert: PipelineRun.AgentId set
        var updatedRun = _runService.GetRun(runId);
        updatedRun.Should().NotBeNull();
        updatedRun!.AgentId.Should().Be("agent-accept");

        // Agent.ActiveJobId set and status == Busy
        agent.ActiveJobId.Should().Be(runId);
        agent.Status.Should().Be(AgentStatus.Busy);

        // Label swapped to InProgress on issue provider (Implementation type)
        _mockLabelSwapper.Verify(l => l.SwapLabelAsync(
            "ip-5", "owner/repo#500", AgentLabels.InProgress,
            LabelTargetKind.Issue, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task AgentAcceptedRunAsync_ReviewType_SwapsLabelOnRepoProvider()
    {
        // Arrange: Create a PipelineRun for a review
        var runId = Guid.NewGuid().ToString();
        var pipelineRun = new PipelineRun
        {
            RunId = runId,
            IssueIdentifier = "42",
            IssueTitle = "Review PR",
            IssueProviderConfigId = "ip-6",
            RepoProviderConfigId = "rp-6",
            StartedAt = DateTime.UtcNow,
            AgentId = null
        };
        _runService.AddRun(pipelineRun);

        var agent = _registry.Register(new AgentRegistrationMessage
        {
            AgentId = "agent-review",
            Hostname = "host-6",
            Labels = new[] { "dotnet" }
        }, "conn-6");

        // Act
        await _lifecycleManager.AgentAcceptedRunAsync(
            runId, "agent-review",
            "42", "ip-6", "rp-6",
            PipelineRunType.Review, CancellationToken.None);

        // Assert: For Review runs, label swap uses repoProviderConfigId + PullRequest target
        _mockLabelSwapper.Verify(l => l.SwapLabelAsync(
            "rp-6", "42", AgentLabels.InProgress,
            LabelTargetKind.PullRequest, It.IsAny<CancellationToken>()), Times.Once);

        agent.ActiveJobId.Should().Be(runId);
        agent.Status.Should().Be(AgentStatus.Busy);
    }

    #endregion

    // ── Test Infrastructure ─────────────────────────────────────────────

    private sealed class InMemoryPipelineDbContext : PipelineDbContext
    {
        public InMemoryPipelineDbContext(DbContextOptions<PipelineDbContext> options)
            : base(options) { }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);

            foreach (var entityType in modelBuilder.Model.GetEntityTypes())
            {
                var rowVersionProp = entityType.FindProperty("RowVersion");
                if (rowVersionProp != null)
                {
                    rowVersionProp.IsConcurrencyToken = false;
                    rowVersionProp.ValueGenerated = Microsoft.EntityFrameworkCore.Metadata.ValueGenerated.Never;
                }
            }

            foreach (var entityType in modelBuilder.Model.GetEntityTypes())
            {
                var indexesToRemove = entityType.GetIndexes()
                    .Where(i => i.GetFilter() != null)
                    .ToList();
                foreach (var index in indexesToRemove)
                {
                    entityType.RemoveIndex(index);
                }
            }
        }
    }

    private sealed class InMemoryDbContextFactory : IDbContextFactory<PipelineDbContext>
    {
        private readonly DbContextOptions<PipelineDbContext> _options;

        public InMemoryDbContextFactory(DbContextOptions<PipelineDbContext> options)
            => _options = options;

        public PipelineDbContext CreateDbContext()
            => new InMemoryPipelineDbContext(_options);

        public Task<PipelineDbContext> CreateDbContextAsync(CancellationToken ct = default)
            => Task.FromResult(CreateDbContext());
    }
}
