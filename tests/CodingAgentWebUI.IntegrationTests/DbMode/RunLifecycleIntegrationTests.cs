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

    #region Test 3b: CancelRunAsync_DeletesK8sJob_WhenJobClientProvided

    [Fact]
    public async Task CancelRunAsync_DeletesK8sJob_WhenJobClientProvided()
    {
        // Arrange: Insert a WorkItem in Running status WITH a K8sJobName
        var runId = Guid.NewGuid();
        const string k8sJobName = "caa-c211d2ad";
        const string k8sNamespace = "coding-agent";

        await using (var db = await _dbFactory.CreateDbContextAsync())
        {
            db.WorkItems.Add(new WorkItemEntity
            {
                Id = runId,
                IssueIdentifier = "owner/repo#350",
                IssueProviderConfigId = "ip-3b",
                Status = WorkItemStatus.Running,
                CreatedAt = DateTimeOffset.UtcNow,
                TaskType = WorkItemTaskType.Implementation,
                K8sJobName = k8sJobName
            });
            await db.SaveChangesAsync();
        }

        // Create a PipelineRun
        var pipelineRun = new PipelineRun
        {
            RunId = runId.ToString(),
            IssueIdentifier = "owner/repo#350",
            IssueTitle = "Cancel K8s Job Test",
            IssueProviderConfigId = "ip-3b",
            RepoProviderConfigId = "rp-3b",
            StartedAt = DateTime.UtcNow,
            AgentId = "agent-cancel-k8s"
        };
        _runService.AddRun(pipelineRun);

        // Register a Busy agent
        var agent = _registry.Register(new AgentRegistrationMessage
        {
            AgentId = "agent-cancel-k8s",
            Hostname = "host-3b",
            Labels = new[] { "dotnet" }
        }, "conn-3b");
        agent.ActiveJobId = runId.ToString();
        _registry.TransitionStatus("agent-cancel-k8s", AgentStatus.Busy);

        // Create lifecycle manager WITH a mock K8s job client
        var mockJobClient = new Mock<IKubernetesJobClient>();
        mockJobClient
            .Setup(c => c.DeleteJobAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var lifecycleWithK8s = new RunLifecycleManager(
            _runService,
            _mockHistoryService.Object,
            _registry,
            _mockLabelSwapper.Object,
            _dispatcher,
            _mockLogger.Object,
            _transitionService,
            _dbFactory,
            mockJobClient.Object,
            k8sNamespace);

        // Act
        var result = await lifecycleWithK8s.CancelRunAsync(runId.ToString(), CancellationToken.None);

        // Assert
        result.Should().NotBeNull();

        // K8s Job deletion was called with the correct job name and namespace
        mockJobClient.Verify(c => c.DeleteJobAsync(
            k8sJobName, k8sNamespace, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task CancelRunAsync_NoK8sJobName_DoesNotCallDeleteJob()
    {
        // Arrange: WorkItem WITHOUT K8sJobName (e.g., SignalR mode)
        var runId = Guid.NewGuid();
        const string k8sNamespace = "coding-agent";

        await using (var db = await _dbFactory.CreateDbContextAsync())
        {
            db.WorkItems.Add(new WorkItemEntity
            {
                Id = runId,
                IssueIdentifier = "owner/repo#351",
                IssueProviderConfigId = "ip-3c",
                Status = WorkItemStatus.Running,
                CreatedAt = DateTimeOffset.UtcNow,
                TaskType = WorkItemTaskType.Implementation,
                K8sJobName = null // No K8s job
            });
            await db.SaveChangesAsync();
        }

        var pipelineRun = new PipelineRun
        {
            RunId = runId.ToString(),
            IssueIdentifier = "owner/repo#351",
            IssueTitle = "Cancel No-K8s Test",
            IssueProviderConfigId = "ip-3c",
            RepoProviderConfigId = "rp-3c",
            StartedAt = DateTime.UtcNow,
            AgentId = "agent-cancel-nok8s"
        };
        _runService.AddRun(pipelineRun);

        var agent = _registry.Register(new AgentRegistrationMessage
        {
            AgentId = "agent-cancel-nok8s",
            Hostname = "host-3c",
            Labels = new[] { "dotnet" }
        }, "conn-3c");
        agent.ActiveJobId = runId.ToString();
        _registry.TransitionStatus("agent-cancel-nok8s", AgentStatus.Busy);

        var mockJobClient = new Mock<IKubernetesJobClient>();

        var lifecycleWithK8s = new RunLifecycleManager(
            _runService,
            _mockHistoryService.Object,
            _registry,
            _mockLabelSwapper.Object,
            _dispatcher,
            _mockLogger.Object,
            _transitionService,
            _dbFactory,
            mockJobClient.Object,
            k8sNamespace);

        // Act
        var result = await lifecycleWithK8s.CancelRunAsync(runId.ToString(), CancellationToken.None);

        // Assert
        result.Should().NotBeNull();
        mockJobClient.Verify(c => c.DeleteJobAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task CancelRunAsync_K8sDeleteReturns404_SucceedsWithoutWarning()
    {
        // Arrange: WorkItem with K8sJobName — but the Job was already deleted (race with ReconciliationService)
        var runId = Guid.NewGuid();
        const string k8sJobName = "caa-already-gone";
        const string k8sNamespace = "coding-agent";

        await using (var db = await _dbFactory.CreateDbContextAsync())
        {
            db.WorkItems.Add(new WorkItemEntity
            {
                Id = runId,
                IssueIdentifier = "owner/repo#352",
                IssueProviderConfigId = "ip-3d",
                Status = WorkItemStatus.Running,
                CreatedAt = DateTimeOffset.UtcNow,
                TaskType = WorkItemTaskType.Implementation,
                K8sJobName = k8sJobName
            });
            await db.SaveChangesAsync();
        }

        var pipelineRun = new PipelineRun
        {
            RunId = runId.ToString(),
            IssueIdentifier = "owner/repo#352",
            IssueTitle = "Cancel 404 Test",
            IssueProviderConfigId = "ip-3d",
            RepoProviderConfigId = "rp-3d",
            StartedAt = DateTime.UtcNow,
            AgentId = "agent-cancel-404"
        };
        _runService.AddRun(pipelineRun);

        var agent = _registry.Register(new AgentRegistrationMessage
        {
            AgentId = "agent-cancel-404",
            Hostname = "host-3d",
            Labels = new[] { "dotnet" }
        }, "conn-3d");
        agent.ActiveJobId = runId.ToString();
        _registry.TransitionStatus("agent-cancel-404", AgentStatus.Busy);

        // Mock throws 404 — simulating Job already deleted by ReconciliationService
        var mockJobClient = new Mock<IKubernetesJobClient>();
        var response404 = new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.NotFound);
        mockJobClient
            .Setup(c => c.DeleteJobAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new k8s.Autorest.HttpOperationException { Response = new k8s.Autorest.HttpResponseMessageWrapper(response404, "") });

        var lifecycleWithK8s = new RunLifecycleManager(
            _runService,
            _mockHistoryService.Object,
            _registry,
            _mockLabelSwapper.Object,
            _dispatcher,
            _mockLogger.Object,
            _transitionService,
            _dbFactory,
            mockJobClient.Object,
            k8sNamespace);

        // Act — should not throw despite 404
        var result = await lifecycleWithK8s.CancelRunAsync(runId.ToString(), CancellationToken.None);

        // Assert: cancel succeeded
        result.Should().NotBeNull();

        // No Warning-level log for 404 (only Debug)
        _mockLogger.Verify(l => l.Warning(
            It.IsAny<Exception>(),
            It.IsAny<string>(),
            It.IsAny<object[]>()), Times.Never);
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

    #region Test: ReportJobCompleted_AfterDeliveryTimeout_TransitionsWorkItemToSucceeded

    /// <summary>
    /// End-to-end scenario: SignalR delivery timeout causes WorkItem to be marked Failed
    /// with InfrastructureFailure, then agent completes → WorkItem ends as Succeeded.
    /// Simulates the primary fix path (else branch in ReportJobCompleted).
    /// </summary>
    [Fact]
    public async Task ReportJobCompleted_AfterDeliveryTimeout_RecoveryTransitionsToSucceeded()
    {
        // Arrange: Insert WorkItem as Dispatched (step 2 of the bug timeline)
        var workItemId = Guid.NewGuid();
        await using (var db = await _dbFactory.CreateDbContextAsync())
        {
            db.WorkItems.Add(new WorkItemEntity
            {
                Id = workItemId,
                IssueIdentifier = "owner/repo#200",
                IssueProviderConfigId = "ip-recovery",
                Status = WorkItemStatus.Dispatched,
                CreatedAt = DateTimeOffset.UtcNow,
                DispatchedAt = DateTimeOffset.UtcNow,
                TaskType = WorkItemTaskType.Implementation
            });
            await db.SaveChangesAsync();
        }

        // Simulate step 4: SignalR delivery timeout catch block transitions to Failed
        var transitionResult = await _transitionService.TransitionAsync(
            workItemId, WorkItemStatus.Failed, item =>
            {
                item.ErrorMessage = "SignalR delivery failure: timeout";
                item.FailureReason = FailureReason.InfrastructureFailure;
                item.CompletedAt = DateTimeOffset.UtcNow;
            });
        transitionResult.Should().BeTrue("dispatch catch block should transition Dispatched → Failed");

        // Verify item is in Failed state
        await using (var db = await _dbFactory.CreateDbContextAsync())
        {
            var item = await db.WorkItems.FindAsync(workItemId);
            item!.Status.Should().Be(WorkItemStatus.Failed);
            item.FailureReason.Should().Be(FailureReason.InfrastructureFailure);
        }

        // Simulate step 7: Agent calls JobAccepted → attempts Failed → Running recovery
        var jobAcceptedResult = await _transitionService.TryRecoverFromInfrastructureFailureAsync(
            workItemId, WorkItemStatus.Running);
        jobAcceptedResult.Should().BeTrue("recovery should allow Failed → Running for infrastructure failures");

        // Simulate step 9: Agent completes, lifecycle manager transitions Running → Succeeded
        // (This path is exercised when CompleteRunAsync runs normally)
        var completionResult = await _transitionService.TransitionAsync(
            workItemId, WorkItemStatus.Succeeded, item =>
            {
                item.CompletedAt = DateTimeOffset.UtcNow;
            });
        completionResult.Should().BeTrue("Running → Succeeded is a valid transition");

        // Final verification: WorkItem is Succeeded
        await using (var db = await _dbFactory.CreateDbContextAsync())
        {
            var item = await db.WorkItems.FindAsync(workItemId);
            item!.Status.Should().Be(WorkItemStatus.Succeeded);
            item.CompletedAt.Should().NotBeNull();
        }
    }

    /// <summary>
    /// End-to-end scenario: Direct recovery from Failed to Succeeded without intermediate Running.
    /// This covers the primary fix path where GetRun returns null and we call
    /// TransitionWorkItemAsync with Succeeded directly (recovery fallback kicks in).
    /// </summary>
    [Fact]
    public async Task DirectRecovery_FailedInfrastructure_ToSucceeded()
    {
        // Arrange: WorkItem already in Failed with InfrastructureFailure
        var workItemId = Guid.NewGuid();
        await using (var db = await _dbFactory.CreateDbContextAsync())
        {
            db.WorkItems.Add(new WorkItemEntity
            {
                Id = workItemId,
                IssueIdentifier = "owner/repo#201",
                IssueProviderConfigId = "ip-direct",
                Status = WorkItemStatus.Failed,
                FailureReason = FailureReason.InfrastructureFailure,
                ErrorMessage = "SignalR delivery failure: timeout",
                CompletedAt = DateTimeOffset.UtcNow.AddMinutes(-1),
                CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-2),
                TaskType = WorkItemTaskType.Implementation
            });
            await db.SaveChangesAsync();
        }

        // Act: Direct recovery to Succeeded (simulates the else-branch fallback path)
        var recovered = await _transitionService.TryRecoverFromInfrastructureFailureAsync(
            workItemId, WorkItemStatus.Succeeded, item =>
            {
                item.CompletedAt = DateTimeOffset.UtcNow;
            });

        // Assert
        recovered.Should().BeTrue();
        await using (var db = await _dbFactory.CreateDbContextAsync())
        {
            var item = await db.WorkItems.FindAsync(workItemId);
            item!.Status.Should().Be(WorkItemStatus.Succeeded);
        }
    }

    /// <summary>
    /// Recovery MUST NOT override legitimate agent errors (only InfrastructureFailure).
    /// </summary>
    [Fact]
    public async Task DirectRecovery_FailedAgentError_Rejected()
    {
        var workItemId = Guid.NewGuid();
        await using (var db = await _dbFactory.CreateDbContextAsync())
        {
            db.WorkItems.Add(new WorkItemEntity
            {
                Id = workItemId,
                IssueIdentifier = "owner/repo#202",
                IssueProviderConfigId = "ip-agent-err",
                Status = WorkItemStatus.Failed,
                FailureReason = FailureReason.AgentError,
                ErrorMessage = "Agent crashed",
                CompletedAt = DateTimeOffset.UtcNow,
                CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-2),
                TaskType = WorkItemTaskType.Implementation
            });
            await db.SaveChangesAsync();
        }

        // Act: Attempt recovery — should be rejected
        var recovered = await _transitionService.TryRecoverFromInfrastructureFailureAsync(
            workItemId, WorkItemStatus.Succeeded);

        // Assert: Item stays Failed
        recovered.Should().BeFalse();
        await using (var db = await _dbFactory.CreateDbContextAsync())
        {
            var item = await db.WorkItems.FindAsync(workItemId);
            item!.Status.Should().Be(WorkItemStatus.Failed);
        }
    }

    /// <summary>
    /// Recovery is idempotent — if the item is already at target, returns true without error.
    /// </summary>
    [Fact]
    public async Task DirectRecovery_AlreadyAtTarget_ReturnsTrue()
    {
        var workItemId = Guid.NewGuid();
        await using (var db = await _dbFactory.CreateDbContextAsync())
        {
            db.WorkItems.Add(new WorkItemEntity
            {
                Id = workItemId,
                IssueIdentifier = "owner/repo#203",
                IssueProviderConfigId = "ip-idempotent",
                Status = WorkItemStatus.Succeeded,
                CompletedAt = DateTimeOffset.UtcNow,
                CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-2),
                TaskType = WorkItemTaskType.Implementation
            });
            await db.SaveChangesAsync();
        }

        // Act: Item is already Succeeded, asking for Succeeded — idempotent
        var recovered = await _transitionService.TryRecoverFromInfrastructureFailureAsync(
            workItemId, WorkItemStatus.Succeeded);

        // Assert: Returns true (idempotent)
        recovered.Should().BeTrue();
    }

    /// <summary>
    /// Recovery does not apply to non-Failed items (e.g., Running or Dispatched).
    /// </summary>
    [Fact]
    public async Task DirectRecovery_NotInFailedState_ReturnsFalse()
    {
        var workItemId = Guid.NewGuid();
        await using (var db = await _dbFactory.CreateDbContextAsync())
        {
            db.WorkItems.Add(new WorkItemEntity
            {
                Id = workItemId,
                IssueIdentifier = "owner/repo#204",
                IssueProviderConfigId = "ip-running",
                Status = WorkItemStatus.Running,
                CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-2),
                TaskType = WorkItemTaskType.Implementation
            });
            await db.SaveChangesAsync();
        }

        // Act: Item is Running, not Failed — recovery not applicable
        var recovered = await _transitionService.TryRecoverFromInfrastructureFailureAsync(
            workItemId, WorkItemStatus.Succeeded);

        // Assert
        recovered.Should().BeFalse();
        await using (var db = await _dbFactory.CreateDbContextAsync())
        {
            var item = await db.WorkItems.FindAsync(workItemId);
            item!.Status.Should().Be(WorkItemStatus.Running); // unchanged
        }
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
