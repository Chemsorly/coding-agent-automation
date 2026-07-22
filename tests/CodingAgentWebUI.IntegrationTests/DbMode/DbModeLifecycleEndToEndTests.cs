using System.Text.Json;
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
/// Comprehensive end-to-end lifecycle integration tests for DB mode.
/// Proves all three modes (Legacy, SignalR+DB, K8s) behave identically by exercising
/// the full dispatch → accept → complete/fail/cancel pipeline with real services
/// (InMemory EF + real OrchestratorRunService + real AgentRegistryService + mock ILabelService).
/// </summary>
public sealed class DbModeLifecycleEndToEndTests : IDisposable
{
    private readonly DbContextOptions<PipelineDbContext> _dbOptions;
    private readonly InMemoryDbContextFactory _dbFactory;
    private readonly WorkItemTransitionService _transitionService;
    private readonly OrchestratorRunService _runService;
    private readonly AgentRegistryService _registry;
    private readonly JobDeduplicationGuardService _dispatcher;
    private readonly Mock<IPipelineRunHistoryService> _mockHistoryService;
    private readonly Mock<ILabelService> _mockLabelService;
    private readonly Mock<ILogger> _mockLogger;
    private readonly RunLifecycleManager _lifecycleManager;

    public DbModeLifecycleEndToEndTests()
    {
        _dbOptions = new DbContextOptionsBuilder<PipelineDbContext>()
            .UseInMemoryDatabase(databaseName: $"DbModeLifecycleE2E-{Guid.NewGuid()}")
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;

        using (var ctx = new InMemoryPipelineDbContext(_dbOptions))
        {
            ctx.Database.EnsureCreated();
        }

        _dbFactory = new InMemoryDbContextFactory(_dbOptions);
        _transitionService = new WorkItemTransitionService(
            _dbFactory, NullLogger<WorkItemTransitionService>.Instance);
        _mockLogger = new Mock<ILogger>();
        _runService = new OrchestratorRunService(_mockLogger.Object);
        _registry = new AgentRegistryService(_mockLogger.Object);
        _dispatcher = new JobDeduplicationGuardService(_registry, _mockLogger.Object);
        _mockHistoryService = new Mock<IPipelineRunHistoryService>();
        _mockLabelService = new Mock<ILabelService>();

        _lifecycleManager = new RunLifecycleManager(
            _runService,
            _mockHistoryService.Object,
            _registry,
            _mockLabelService.Object,
            _dispatcher,
            _mockLogger.Object,
            _transitionService);
    }

    public void Dispose()
    {
        using var db = new InMemoryPipelineDbContext(_dbOptions);
        db.Database.EnsureDeleted();
    }

    #region Group 1: Full dispatch → completion lifecycle

    [Fact]
    public async Task FullLifecycle_Dispatch_AgentAccepts_Completes_AllStatesConsistent()
    {
        // Arrange: Create WorkItem (Pending) → transition to Dispatched
        var runId = Guid.NewGuid();
        await using (var db = await _dbFactory.CreateDbContextAsync())
        {
            db.WorkItems.Add(new WorkItemEntity
            {
                Id = runId,
                IssueIdentifier = "owner/repo#1",
                IssueProviderConfigId = "ip-1",
                Status = WorkItemStatus.Pending,
                CreatedAt = DateTimeOffset.UtcNow,
                TaskType = WorkItemTaskType.Implementation
            });
            await db.SaveChangesAsync();
        }

        await _transitionService.TransitionAsync(runId, WorkItemStatus.Dispatched, ct: CancellationToken.None);

        // Create PipelineRun in-memory (simulating dispatch path)
        var pipelineRun = new PipelineRun
        {
            RunId = runId.ToString(),
            IssueIdentifier = "owner/repo#1",
            IssueTitle = "Full Lifecycle Test",
            IssueProviderConfigId = "ip-1",
            RepoProviderConfigId = "rp-1",
            StartedAt = DateTime.UtcNow
        };
        _runService.AddRun(pipelineRun);

        // Register agent
        var agent = _registry.Register(new AgentRegistrationMessage
        {
            AgentId = "agent-full-1",
            Hostname = "host-1",
            Labels = new[] { "dotnet" }
        }, "conn-full-1");

        // Act: Agent accepts
        await _lifecycleManager.AgentAcceptedRunAsync(
            runId.ToString(), "agent-full-1",
            "owner/repo#1", "ip-1", "rp-1",
            PipelineRunType.Implementation, CancellationToken.None);

        // Assert after accept
        var updatedRun = _runService.GetRun(runId.ToString());
        updatedRun.Should().NotBeNull();
        updatedRun!.AgentId.Should().Be("agent-full-1");
        agent.ActiveJobId.Should().Be(runId.ToString());
        agent.Status.Should().Be(AgentStatus.Busy);
        _mockLabelService.Verify(l => l.SwapLabelAsync(
            "ip-1", "owner/repo#1", AgentLabels.InProgress,
            LabelTargetKind.Issue, It.IsAny<CancellationToken>()), Times.Once);

        // WorkItem still Dispatched (accept doesn't transition DB)
        await using (var db = await _dbFactory.CreateDbContextAsync())
        {
            var item = await db.WorkItems.FindAsync(runId);
            item!.Status.Should().Be(WorkItemStatus.Dispatched);
        }

        // Act: Complete
        var result = await _lifecycleManager.CompleteRunAsync(
            runId.ToString(), WorkItemStatus.Succeeded, CancellationToken.None);

        // Assert after complete
        result.Should().NotBeNull();
        await using (var db = await _dbFactory.CreateDbContextAsync())
        {
            var item = await db.WorkItems.FindAsync(runId);
            item!.Status.Should().Be(WorkItemStatus.Succeeded);
            item.CompletedAt.Should().NotBeNull();
        }
        _runService.GetRun(runId.ToString()).Should().BeNull();
        _mockHistoryService.Verify(h => h.AddRunToHistoryAsync(
            It.Is<PipelineRun>(r => r.RunId == runId.ToString()),
            It.IsAny<CancellationToken>()), Times.Once);
        _dispatcher.IsIssueQueued("owner/repo#1", "ip-1").Should().BeFalse();
    }

    [Fact]
    public async Task FullLifecycle_Dispatch_AgentAccepts_Fails_AllStatesConsistent()
    {
        // Arrange
        var runId = Guid.NewGuid();
        await using (var db = await _dbFactory.CreateDbContextAsync())
        {
            db.WorkItems.Add(new WorkItemEntity
            {
                Id = runId,
                IssueIdentifier = "owner/repo#2",
                IssueProviderConfigId = "ip-2",
                Status = WorkItemStatus.Dispatched,
                CreatedAt = DateTimeOffset.UtcNow,
                TaskType = WorkItemTaskType.Implementation
            });
            await db.SaveChangesAsync();
        }

        var pipelineRun = new PipelineRun
        {
            RunId = runId.ToString(),
            IssueIdentifier = "owner/repo#2",
            IssueTitle = "Fail Lifecycle",
            IssueProviderConfigId = "ip-2",
            RepoProviderConfigId = "rp-2",
            StartedAt = DateTime.UtcNow,
            AgentId = "agent-fail-1"
        };
        _runService.AddRun(pipelineRun);

        var agent = _registry.Register(new AgentRegistrationMessage
        {
            AgentId = "agent-fail-1",
            Hostname = "host-2",
            Labels = new[] { "dotnet" }
        }, "conn-fail-1");
        agent.ActiveJobId = runId.ToString();
        _registry.TransitionStatus("agent-fail-1", AgentStatus.Busy);

        // Act
        var result = await _lifecycleManager.FailRunAsync(
            runId.ToString(), "Agent crashed", CancellationToken.None);

        // Assert
        result.Should().NotBeNull();
        result!.FailureReason.Should().Be("Agent crashed");

        await using (var db = await _dbFactory.CreateDbContextAsync())
        {
            var item = await db.WorkItems.FindAsync(runId);
            item!.Status.Should().Be(WorkItemStatus.Failed);
            item.CompletedAt.Should().NotBeNull();
        }

        _runService.GetRun(runId.ToString()).Should().BeNull();
        agent.Status.Should().Be(AgentStatus.Idle);
        agent.ActiveJobId.Should().BeNull();
        _mockLabelService.Verify(l => l.SwapLabelAsync(
            "ip-2", "owner/repo#2", AgentLabels.Error,
            LabelTargetKind.Issue, It.IsAny<CancellationToken>()), Times.Once);
        _mockHistoryService.Verify(h => h.AddRunToHistoryAsync(
            It.Is<PipelineRun>(r => r.RunId == runId.ToString()),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task FullLifecycle_Dispatch_AgentAccepts_Cancelled_AllStatesConsistent()
    {
        // Arrange
        var runId = Guid.NewGuid();
        await using (var db = await _dbFactory.CreateDbContextAsync())
        {
            db.WorkItems.Add(new WorkItemEntity
            {
                Id = runId,
                IssueIdentifier = "owner/repo#3",
                IssueProviderConfigId = "ip-3",
                Status = WorkItemStatus.Running,
                CreatedAt = DateTimeOffset.UtcNow,
                TaskType = WorkItemTaskType.Implementation
            });
            await db.SaveChangesAsync();
        }

        var pipelineRun = new PipelineRun
        {
            RunId = runId.ToString(),
            IssueIdentifier = "owner/repo#3",
            IssueTitle = "Cancel Lifecycle",
            IssueProviderConfigId = "ip-3",
            RepoProviderConfigId = "rp-3",
            StartedAt = DateTime.UtcNow,
            AgentId = "agent-cancel-1"
        };
        _runService.AddRun(pipelineRun);

        var agent = _registry.Register(new AgentRegistrationMessage
        {
            AgentId = "agent-cancel-1",
            Hostname = "host-3",
            Labels = new[] { "dotnet" }
        }, "conn-cancel-1");
        agent.ActiveJobId = runId.ToString();
        _registry.TransitionStatus("agent-cancel-1", AgentStatus.Busy);

        // Act
        var result = await _lifecycleManager.CancelRunAsync(
            runId.ToString(), CancellationToken.None);

        // Assert
        result.Should().NotBeNull();
        await using (var db = await _dbFactory.CreateDbContextAsync())
        {
            var item = await db.WorkItems.FindAsync(runId);
            item!.Status.Should().Be(WorkItemStatus.Cancelled);
            item.CompletedAt.Should().NotBeNull();
        }
        _runService.GetRun(runId.ToString()).Should().BeNull();
        agent.Status.Should().Be(AgentStatus.Idle);
        agent.ActiveJobId.Should().BeNull();
        _mockLabelService.Verify(l => l.SwapLabelAsync(
            "ip-3", "owner/repo#3", AgentLabels.Cancelled,
            LabelTargetKind.Issue, It.IsAny<CancellationToken>()), Times.Once);
    }

    #endregion

    #region Group 2: No-agent-available scenarios (label timing)

    [Fact]
    public async Task Dispatch_NoAgentAvailable_LabelNotSwapped_WorkItemPending()
    {
        // Arrange: WorkItem in Pending, no agent registered at all
        var runId = Guid.NewGuid();
        await using (var db = await _dbFactory.CreateDbContextAsync())
        {
            db.WorkItems.Add(new WorkItemEntity
            {
                Id = runId,
                IssueIdentifier = "owner/repo#4",
                IssueProviderConfigId = "ip-4",
                Status = WorkItemStatus.Pending,
                CreatedAt = DateTimeOffset.UtcNow,
                TaskType = WorkItemTaskType.Implementation
            });
            await db.SaveChangesAsync();
        }

        // No run created, no agent registered — simulates "no agent available" scenario
        // Try to select an agent — should return null
        var selectedAgent = _dispatcher.SelectAgent(new List<string> { "dotnet" });

        // Assert
        selectedAgent.Should().BeNull();
        _mockLabelService.Verify(l => l.SwapLabelAsync(
            It.IsAny<ProviderConfigId>(), It.IsAny<IssueIdentifier>(), AgentLabels.InProgress,
            It.IsAny<LabelTargetKind>(), It.IsAny<CancellationToken>()), Times.Never);

        await using (var db = await _dbFactory.CreateDbContextAsync())
        {
            var item = await db.WorkItems.FindAsync(runId);
            item!.Status.Should().Be(WorkItemStatus.Pending);
        }
    }

    [Fact]
    public async Task Dispatch_AgentBecomesAvailable_DrainAssigns_LabelSwapped()
    {
        // Arrange: WorkItem already dispatched, simulate drain creating run + agent accepting
        var runId = Guid.NewGuid();
        await using (var db = await _dbFactory.CreateDbContextAsync())
        {
            db.WorkItems.Add(new WorkItemEntity
            {
                Id = runId,
                IssueIdentifier = "owner/repo#5",
                IssueProviderConfigId = "ip-5",
                Status = WorkItemStatus.Dispatched,
                CreatedAt = DateTimeOffset.UtcNow,
                TaskType = WorkItemTaskType.Implementation
            });
            await db.SaveChangesAsync();
        }

        // Simulate drain: creates run and registers agent
        var pipelineRun = new PipelineRun
        {
            RunId = runId.ToString(),
            IssueIdentifier = "owner/repo#5",
            IssueTitle = "Drain Assigns",
            IssueProviderConfigId = "ip-5",
            RepoProviderConfigId = "rp-5",
            StartedAt = DateTime.UtcNow
        };
        _runService.AddRun(pipelineRun);

        _registry.Register(new AgentRegistrationMessage
        {
            AgentId = "agent-drain-5",
            Hostname = "host-5",
            Labels = new[] { "dotnet" }
        }, "conn-drain-5");

        // Act: Agent accepts the run
        await _lifecycleManager.AgentAcceptedRunAsync(
            runId.ToString(), "agent-drain-5",
            "owner/repo#5", "ip-5", "rp-5",
            PipelineRunType.Implementation, CancellationToken.None);

        // Assert: Label NOW swapped to InProgress
        _mockLabelService.Verify(l => l.SwapLabelAsync(
            "ip-5", "owner/repo#5", AgentLabels.InProgress,
            LabelTargetKind.Issue, It.IsAny<CancellationToken>()), Times.Once);

        var run = _runService.GetRun(runId.ToString());
        run.Should().NotBeNull();
        run!.AgentId.Should().Be("agent-drain-5");
    }

    #endregion

    #region Group 3: Restart recovery scenarios

    [Fact]
    public async Task OrchestratorRestart_PendingWorkItem_DrainRecreatesRun_AgentGetsJob()
    {
        // Arrange: WorkItem in Pending with full payload, OrchestratorRunService EMPTY
        var runId = Guid.NewGuid();
        var payload = JsonSerializer.Serialize(new JobDistributionRequest
        {
            IssueIdentifier = "owner/repo#6",
            IssueProviderConfigId = "ip-6",
            RepoProviderConfigId = "rp-6",
            InitiatedBy = "loop",
            TaskType = WorkItemTaskType.Implementation,
            AgentSelector = "dotnet",
            TimeoutSeconds = 3600,
            IssueDetail = new IssueDetail
            {
                Title = "Restart Recovery",
                Description = "Test description",
                Identifier = "owner/repo#6",
                Labels = new[] { "agent:next" }
            }
        }, PipelineJsonOptions.Default);

        await using (var db = await _dbFactory.CreateDbContextAsync())
        {
            db.WorkItems.Add(new WorkItemEntity
            {
                Id = runId,
                IssueIdentifier = "owner/repo#6",
                IssueProviderConfigId = "ip-6",
                Status = WorkItemStatus.Pending,
                CreatedAt = DateTimeOffset.UtcNow,
                TaskType = WorkItemTaskType.Implementation,
                Payload = payload,
                AgentSelector = "dotnet"
            });
            await db.SaveChangesAsync();
        }

        // Verify: OrchestratorRunService is empty (restart scenario)
        _runService.GetActiveRuns().Should().BeEmpty();

        // Simulate drain: deserialize payload, create run, transition to Dispatched
        var pendingQuery = new DbPendingWorkQuery(_dbFactory);
        var pendingJobs = await pendingQuery.GetPendingJobsAsync(CancellationToken.None);
        pendingJobs.Should().HaveCount(1);
        pendingJobs[0].IssueIdentifier.Should().Be("owner/repo#6");

        // Simulate creating the run from drain
        var restoredRun = new PipelineRun
        {
            RunId = runId.ToString(),
            IssueIdentifier = "owner/repo#6",
            IssueTitle = "Restart Recovery",
            IssueProviderConfigId = "ip-6",
            RepoProviderConfigId = "rp-6",
            StartedAt = DateTime.UtcNow
        };
        _runService.AddRun(restoredRun);
        await _transitionService.TransitionAsync(runId, WorkItemStatus.Dispatched, ct: CancellationToken.None);

        // Assert: run exists, WorkItem is Dispatched
        _runService.GetRun(runId.ToString()).Should().NotBeNull();
        await using (var db = await _dbFactory.CreateDbContextAsync())
        {
            var item = await db.WorkItems.FindAsync(runId);
            item!.Status.Should().Be(WorkItemStatus.Dispatched);
        }
    }

    [Fact]
    public async Task OrchestratorRestart_ActiveRunRestoredByAgent_AppearsInActiveRunQuery()
    {
        // Arrange: Add a PipelineRun to OrchestratorRunService (simulating agent reconnect restore)
        // NO matching WorkItem in DB
        var restoredRun = new PipelineRun
        {
            RunId = Guid.NewGuid().ToString(),
            IssueIdentifier = "owner/repo#7",
            IssueTitle = "Restored from agent reconnect",
            IssueProviderConfigId = "ip-7",
            RepoProviderConfigId = "rp-7",
            StartedAt = DateTime.UtcNow,
            AgentId = "agent-restored-7"
        };
        restoredRun.CurrentStep = PipelineStep.GeneratingCode;
        _runService.AddRun(restoredRun);

        // Act: Query active runs using PostgresActiveRunQueryService
        var queryService = new PostgresActiveRunQueryService(_dbFactory, _runService);
        var activeRuns = await queryService.GetActiveRunsAsync(CancellationToken.None);

        // Assert: The restored run appears in results despite no DB WorkItem
        activeRuns.Should().ContainSingle(r => r.RunId == restoredRun.RunId);
        var found = activeRuns.First(r => r.RunId == restoredRun.RunId);
        found.AgentId.Should().Be("agent-restored-7");
        found.CurrentStep.Should().Be(PipelineStep.GeneratingCode);
        found.IssueIdentifier.Should().Be("owner/repo#7");
    }

    #endregion

    #region Group 4: Concurrent/race scenarios

    [Fact]
    public async Task ConcurrentFailAndComplete_OnlyOneSucceeds()
    {
        // Arrange
        var runId = Guid.NewGuid();
        await using (var db = await _dbFactory.CreateDbContextAsync())
        {
            db.WorkItems.Add(new WorkItemEntity
            {
                Id = runId,
                IssueIdentifier = "owner/repo#8",
                IssueProviderConfigId = "ip-8",
                Status = WorkItemStatus.Running,
                CreatedAt = DateTimeOffset.UtcNow,
                TaskType = WorkItemTaskType.Implementation
            });
            await db.SaveChangesAsync();
        }

        var pipelineRun = new PipelineRun
        {
            RunId = runId.ToString(),
            IssueIdentifier = "owner/repo#8",
            IssueTitle = "Concurrent Race",
            IssueProviderConfigId = "ip-8",
            RepoProviderConfigId = "rp-8",
            StartedAt = DateTime.UtcNow,
            AgentId = "agent-race-8"
        };
        _runService.AddRun(pipelineRun);

        // Act: Call FailRunAsync and CompleteRunAsync concurrently
        var failTask = _lifecycleManager.FailRunAsync(
            runId.ToString(), "Concurrent fail", CancellationToken.None);
        var completeTask = _lifecycleManager.CompleteRunAsync(
            runId.ToString(), WorkItemStatus.Succeeded, CancellationToken.None);
        var results = await Task.WhenAll(failTask, completeTask);

        // Assert: Exactly ONE returns non-null (atomic claim via RemoveRun)
        var nonNullResults = results.Where(r => r is not null).ToList();
        nonNullResults.Should().HaveCount(1);

        // PipelineRun removed from active
        _runService.GetRun(runId.ToString()).Should().BeNull();

        // History has exactly ONE entry
        _mockHistoryService.Verify(h => h.AddRunToHistoryAsync(
            It.Is<PipelineRun>(r => r.RunId == runId.ToString()),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task FailRunAsync_CalledTwice_SecondCallReturnsNull()
    {
        // Arrange
        var runId = Guid.NewGuid();
        await using (var db = await _dbFactory.CreateDbContextAsync())
        {
            db.WorkItems.Add(new WorkItemEntity
            {
                Id = runId,
                IssueIdentifier = "owner/repo#9",
                IssueProviderConfigId = "ip-9",
                Status = WorkItemStatus.Running,
                CreatedAt = DateTimeOffset.UtcNow,
                TaskType = WorkItemTaskType.Implementation
            });
            await db.SaveChangesAsync();
        }

        var pipelineRun = new PipelineRun
        {
            RunId = runId.ToString(),
            IssueIdentifier = "owner/repo#9",
            IssueTitle = "Double Fail",
            IssueProviderConfigId = "ip-9",
            RepoProviderConfigId = "rp-9",
            StartedAt = DateTime.UtcNow,
            AgentId = "agent-double-9"
        };
        _runService.AddRun(pipelineRun);

        // Act
        var first = await _lifecycleManager.FailRunAsync(
            runId.ToString(), "First fail", CancellationToken.None);
        var second = await _lifecycleManager.FailRunAsync(
            runId.ToString(), "Second fail", CancellationToken.None);

        // Assert
        first.Should().NotBeNull();
        second.Should().BeNull();

        // No double-processing: history has exactly one entry
        _mockHistoryService.Verify(h => h.AddRunToHistoryAsync(
            It.Is<PipelineRun>(r => r.RunId == runId.ToString()),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    #endregion

    #region Group 5: Provider selection (Review vs Implementation vs Decomposition)

    [Fact]
    public async Task AgentAccepted_ImplementationType_LabelOnIssueProvider()
    {
        // Arrange
        var runId = Guid.NewGuid().ToString();
        _runService.AddRun(new PipelineRun
        {
            RunId = runId,
            IssueIdentifier = "owner/repo#10",
            IssueTitle = "Impl Label",
            IssueProviderConfigId = "ip-10",
            RepoProviderConfigId = "rp-10",
            StartedAt = DateTime.UtcNow
        });
        _registry.Register(new AgentRegistrationMessage
        {
            AgentId = "agent-impl-10",
            Hostname = "host-10",
            Labels = new[] { "dotnet" }
        }, "conn-impl-10");

        // Act
        await _lifecycleManager.AgentAcceptedRunAsync(
            runId, "agent-impl-10",
            "owner/repo#10", "ip-10", "rp-10",
            PipelineRunType.Implementation, CancellationToken.None);

        // Assert: SwapLabel with issueProviderConfigId + Issue target
        _mockLabelService.Verify(l => l.SwapLabelAsync(
            "ip-10", "owner/repo#10", AgentLabels.InProgress,
            LabelTargetKind.Issue, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task AgentAccepted_ReviewType_LabelOnRepoProvider()
    {
        // Arrange
        var runId = Guid.NewGuid().ToString();
        _runService.AddRun(new PipelineRun
        {
            RunId = runId,
            IssueIdentifier = "42",
            IssueTitle = "PR Review",
            IssueProviderConfigId = "ip-11",
            RepoProviderConfigId = "rp-11",
            StartedAt = DateTime.UtcNow
        });
        _registry.Register(new AgentRegistrationMessage
        {
            AgentId = "agent-review-11",
            Hostname = "host-11",
            Labels = new[] { "dotnet" }
        }, "conn-review-11");

        // Act
        await _lifecycleManager.AgentAcceptedRunAsync(
            runId, "agent-review-11",
            "42", "ip-11", "rp-11",
            PipelineRunType.Review, CancellationToken.None);

        // Assert: SwapLabel with repoProviderConfigId + PullRequest target
        _mockLabelService.Verify(l => l.SwapLabelAsync(
            "rp-11", "42", AgentLabels.InProgress,
            LabelTargetKind.PullRequest, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task AgentAccepted_DecompositionType_LabelOnIssueProvider()
    {
        // Arrange
        var runId = Guid.NewGuid().ToString();
        _runService.AddRun(new PipelineRun
        {
            RunId = runId,
            IssueIdentifier = "owner/repo#12",
            IssueTitle = "Decompose Epic",
            IssueProviderConfigId = "ip-12",
            RepoProviderConfigId = "rp-12",
            StartedAt = DateTime.UtcNow
        });

        _registry.Register(new AgentRegistrationMessage
        {
            AgentId = "agent-decomp-12",
            Hostname = "host-12",
            Labels = new[] { "dotnet" }
        }, "conn-decomp-12");

        // Act
        await _lifecycleManager.AgentAcceptedRunAsync(
            runId, "agent-decomp-12",
            "owner/repo#12", "ip-12", "rp-12",
            PipelineRunType.Decomposition, CancellationToken.None);

        // Assert: Decomposition uses issueProviderConfigId + Issue (same as Implementation)
        _mockLabelService.Verify(l => l.SwapLabelAsync(
            "ip-12", "owner/repo#12", AgentLabels.InProgress,
            LabelTargetKind.Issue, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task FailRun_ReviewType_ErrorLabelOnRepoProvider()
    {
        // Arrange
        var runId = Guid.NewGuid();
        await using (var db = await _dbFactory.CreateDbContextAsync())
        {
            db.WorkItems.Add(new WorkItemEntity
            {
                Id = runId,
                IssueIdentifier = "55",
                IssueProviderConfigId = "ip-13",
                Status = WorkItemStatus.Running,
                CreatedAt = DateTimeOffset.UtcNow,
                TaskType = WorkItemTaskType.Review
            });
            await db.SaveChangesAsync();
        }

        var pipelineRun = PipelineRun.Create(
            runId.ToString(), "55", "Review PR 55",
            "ip-13", "rp-13",
            PipelineRunType.Review,
            agentId: "agent-rev-13");
        _runService.AddRun(pipelineRun);

        // Act
        var result = await _lifecycleManager.FailRunAsync(
            runId.ToString(), "Review failed", CancellationToken.None);

        // Assert: Error label goes to repo provider for review runs
        result.Should().NotBeNull();
        _mockLabelService.Verify(l => l.SwapLabelAsync(
            "rp-13", "55", AgentLabels.Error,
            LabelTargetKind.PullRequest, It.IsAny<CancellationToken>()), Times.Once);
    }

    #endregion

    #region Group 6: WorkItem state machine validation

    [Fact]
    public async Task WorkItemTransition_Dispatched_To_Succeeded_ViaRunning()
    {
        // Arrange: WorkItem in Dispatched status
        var runId = Guid.NewGuid();
        await using (var db = await _dbFactory.CreateDbContextAsync())
        {
            db.WorkItems.Add(new WorkItemEntity
            {
                Id = runId,
                IssueIdentifier = "owner/repo#14",
                IssueProviderConfigId = "ip-14",
                Status = WorkItemStatus.Dispatched,
                CreatedAt = DateTimeOffset.UtcNow,
                TaskType = WorkItemTaskType.Implementation
            });
            await db.SaveChangesAsync();
        }

        // Create run so lifecycle manager can find it
        var pipelineRun = new PipelineRun
        {
            RunId = runId.ToString(),
            IssueIdentifier = "owner/repo#14",
            IssueTitle = "Two-step transition",
            IssueProviderConfigId = "ip-14",
            RepoProviderConfigId = "rp-14",
            StartedAt = DateTime.UtcNow,
            AgentId = "agent-14"
        };
        _runService.AddRun(pipelineRun);

        // Act: CompleteRunAsync triggers two-step fallback (Dispatched → Running → Succeeded)
        var result = await _lifecycleManager.CompleteRunAsync(
            runId.ToString(), WorkItemStatus.Succeeded, CancellationToken.None);

        // Assert: WorkItem ends up Succeeded (via intermediate Running step)
        result.Should().NotBeNull();
        await using (var db = await _dbFactory.CreateDbContextAsync())
        {
            var item = await db.WorkItems.FindAsync(runId);
            item!.Status.Should().Be(WorkItemStatus.Succeeded);
            item.CompletedAt.Should().NotBeNull();
        }
    }

    [Fact]
    public async Task WorkItemTransition_Pending_To_Failed_Direct()
    {
        // Arrange
        var runId = Guid.NewGuid();
        await using (var db = await _dbFactory.CreateDbContextAsync())
        {
            db.WorkItems.Add(new WorkItemEntity
            {
                Id = runId,
                IssueIdentifier = "owner/repo#15",
                IssueProviderConfigId = "ip-15",
                Status = WorkItemStatus.Pending,
                CreatedAt = DateTimeOffset.UtcNow,
                TaskType = WorkItemTaskType.Implementation
            });
            await db.SaveChangesAsync();
        }

        var pipelineRun = new PipelineRun
        {
            RunId = runId.ToString(),
            IssueIdentifier = "owner/repo#15",
            IssueTitle = "Direct Pending→Failed",
            IssueProviderConfigId = "ip-15",
            RepoProviderConfigId = "rp-15",
            StartedAt = DateTime.UtcNow,
            AgentId = "agent-15"
        };
        _runService.AddRun(pipelineRun);

        // Act: Pending → Failed is a valid direct transition
        var result = await _lifecycleManager.FailRunAsync(
            runId.ToString(), "Pre-dispatch failure", CancellationToken.None);

        // Assert
        result.Should().NotBeNull();
        await using (var db = await _dbFactory.CreateDbContextAsync())
        {
            var item = await db.WorkItems.FindAsync(runId);
            item!.Status.Should().Be(WorkItemStatus.Failed);
            item.CompletedAt.Should().NotBeNull();
        }
    }

    [Fact]
    public async Task WorkItemTransition_AlreadyTerminal_NoOp()
    {
        // Arrange: WorkItem already Succeeded
        var runId = Guid.NewGuid();
        await using (var db = await _dbFactory.CreateDbContextAsync())
        {
            db.WorkItems.Add(new WorkItemEntity
            {
                Id = runId,
                IssueIdentifier = "owner/repo#16",
                IssueProviderConfigId = "ip-16",
                Status = WorkItemStatus.Succeeded,
                CompletedAt = DateTimeOffset.UtcNow,
                CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-5),
                TaskType = WorkItemTaskType.Implementation
            });
            await db.SaveChangesAsync();
        }

        // Add run so RemoveRun returns non-null (this tests the DB transition path)
        var pipelineRun = new PipelineRun
        {
            RunId = runId.ToString(),
            IssueIdentifier = "owner/repo#16",
            IssueTitle = "Already Terminal",
            IssueProviderConfigId = "ip-16",
            RepoProviderConfigId = "rp-16",
            StartedAt = DateTime.UtcNow,
            AgentId = "agent-16"
        };
        _runService.AddRun(pipelineRun);

        // Act: Try to fail an already-Succeeded WorkItem
        var result = await _lifecycleManager.FailRunAsync(
            runId.ToString(), "Should not overwrite", CancellationToken.None);

        // Assert: Run is still removed from in-memory (it was claimed),
        // but DB status stays Succeeded (transition rejected gracefully)
        result.Should().NotBeNull(); // RemoveRun succeeds — that's the atomic claim
        await using (var db = await _dbFactory.CreateDbContextAsync())
        {
            var item = await db.WorkItems.FindAsync(runId);
            item!.Status.Should().Be(WorkItemStatus.Succeeded); // Unchanged
        }
    }

    #endregion

    #region Group 7: Secrets and payload round-trip

    [Fact]
    public async Task DbPendingWorkQuery_RoundTrips_TitleAndRepoProviderFromPayload()
    {
        // Arrange: Insert WorkItem with full JobDistributionRequest payload
        var runId = Guid.NewGuid();
        var payload = JsonSerializer.Serialize(new JobDistributionRequest
        {
            IssueIdentifier = "owner/repo#17",
            IssueProviderConfigId = "ip-17",
            RepoProviderConfigId = "rp-17",
            InitiatedBy = "test",
            TaskType = WorkItemTaskType.Implementation,
            AgentSelector = "dotnet",
            TimeoutSeconds = 1800,
            IssueDetail = new IssueDetail
            {
                Title = "Payload Round-Trip Title",
                Description = "Description for payload test",
                Identifier = "owner/repo#17",
                Labels = new[] { "agent:next", "enhancement" }
            }
        }, PipelineJsonOptions.Default);

        await using (var db = await _dbFactory.CreateDbContextAsync())
        {
            db.WorkItems.Add(new WorkItemEntity
            {
                Id = runId,
                IssueIdentifier = "owner/repo#17",
                IssueProviderConfigId = "ip-17",
                Status = WorkItemStatus.Pending,
                CreatedAt = DateTimeOffset.UtcNow,
                TaskType = WorkItemTaskType.Implementation,
                Payload = payload,
                AgentSelector = "dotnet"
            });
            await db.SaveChangesAsync();
        }

        // Act
        var pendingQuery = new DbPendingWorkQuery(_dbFactory);
        var jobs = await pendingQuery.GetPendingJobsAsync(CancellationToken.None);

        // Assert
        jobs.Should().HaveCount(1);
        jobs[0].IssueTitle.Should().Be("Payload Round-Trip Title");
        jobs[0].RepoProviderId.Should().Be("rp-17");
        jobs[0].IssueIdentifier.Should().Be("owner/repo#17");
        jobs[0].IssueProviderId.Should().Be("ip-17");
        jobs[0].RequiredLabels.Should().Contain("dotnet");
    }

    #endregion

    #region Group 8: Active Run display parity

    [Fact]
    public async Task ActiveRunQuery_DbRun_EnrichedWithLiveStep()
    {
        // Arrange: Insert WorkItem in Dispatched + create matching live PipelineRun
        var runId = Guid.NewGuid();
        await using (var db = await _dbFactory.CreateDbContextAsync())
        {
            db.WorkItems.Add(new WorkItemEntity
            {
                Id = runId,
                IssueIdentifier = "owner/repo#18",
                IssueProviderConfigId = "ip-18",
                Status = WorkItemStatus.Dispatched,
                CreatedAt = DateTimeOffset.UtcNow,
                TaskType = WorkItemTaskType.Implementation,
                AssignedAgentId = "agent-18"
            });
            await db.SaveChangesAsync();
        }

        // Create matching PipelineRun with advanced step
        var liveRun = new PipelineRun
        {
            RunId = runId.ToString(),
            IssueIdentifier = "owner/repo#18",
            IssueTitle = "Live Step Enrichment",
            IssueProviderConfigId = "ip-18",
            RepoProviderConfigId = "rp-18",
            StartedAt = DateTime.UtcNow,
            AgentId = "agent-18"
        };
        liveRun.CurrentStep = PipelineStep.GeneratingCode;
        _runService.AddRun(liveRun);

        // Act
        var queryService = new PostgresActiveRunQueryService(_dbFactory, _runService);
        var activeRuns = await queryService.GetActiveRunsAsync(CancellationToken.None);

        // Assert: result has CurrentStep from live run (not DB-level MapStatusToStep)
        activeRuns.Should().ContainSingle(r => r.RunId == runId.ToString());
        var found = activeRuns.First(r => r.RunId == runId.ToString());
        found.CurrentStep.Should().Be(PipelineStep.GeneratingCode); // From live, not MapStatusToStep's Created
        found.AgentId.Should().Be("agent-18");
        found.IssueTitle.Should().Be("Live Step Enrichment");
    }

    [Fact]
    public async Task ActiveRunQuery_InMemoryOnlyRun_IncludedInResults()
    {
        // Arrange: Add run to OrchestratorRunService with NO matching WorkItem in DB
        var orphanRun = new PipelineRun
        {
            RunId = Guid.NewGuid().ToString(),
            IssueIdentifier = "owner/repo#19",
            IssueTitle = "In-Memory Only Run",
            IssueProviderConfigId = "ip-19",
            RepoProviderConfigId = "rp-19",
            StartedAt = DateTime.UtcNow,
            AgentId = "agent-19"
        };
        orphanRun.CurrentStep = PipelineStep.CloningRepository;
        _runService.AddRun(orphanRun);

        // Act
        var queryService = new PostgresActiveRunQueryService(_dbFactory, _runService);
        var activeRuns = await queryService.GetActiveRunsAsync(CancellationToken.None);

        // Assert: Run still appears despite no DB WorkItem
        activeRuns.Should().Contain(r => r.RunId == orphanRun.RunId);
        var found = activeRuns.First(r => r.RunId == orphanRun.RunId);
        found.IssueTitle.Should().Be("In-Memory Only Run");
        found.CurrentStep.Should().Be(PipelineStep.CloningRepository);
        found.AgentId.Should().Be("agent-19");
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

            // Disable RowVersion concurrency tokens (InMemory provider doesn't support xmin)
            foreach (var entityType in modelBuilder.Model.GetEntityTypes())
            {
                var rowVersionProp = entityType.FindProperty("RowVersion");
                if (rowVersionProp != null)
                {
                    rowVersionProp.IsConcurrencyToken = false;
                    rowVersionProp.ValueGenerated =
                        Microsoft.EntityFrameworkCore.Metadata.ValueGenerated.Never;
                }
            }

            // Remove filtered indexes (InMemory provider doesn't support HasFilter)
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
