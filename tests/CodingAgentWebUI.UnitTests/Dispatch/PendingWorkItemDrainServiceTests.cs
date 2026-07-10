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
/// Tests for <see cref="PendingWorkItemDrainService"/> label swap behavior (#997).
/// Verifies that the drain service swaps the issue label to agent:in-progress
/// only after successful SignalR delivery to an agent.
/// </summary>
public sealed class PendingWorkItemDrainServiceTests : IDisposable
{
    private readonly DbContextOptions<PipelineDbContext> _dbOptions;
    private readonly InMemoryDbContextFactory _dbFactory;
    private readonly Mock<ISignalRWorkDistributorAgentResolver> _mockResolver = new();
    private readonly Mock<IAgentCommunication> _mockAgentComm = new();
    private readonly Mock<ILabelSwapper> _mockLabelSwapper = new();
    private readonly Mock<IPendingWorkQuery> _mockPendingWork = new();
    private readonly Mock<IProjectStore> _mockProjectStore = new();
    private readonly OrchestratorRunService _runService;
    private readonly WorkItemTransitionService _transitionService;

    public PendingWorkItemDrainServiceTests()
    {
        _dbOptions = new DbContextOptionsBuilder<PipelineDbContext>()
            .UseInMemoryDatabase($"DrainTest_{Guid.NewGuid()}")
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        _dbFactory = new InMemoryDbContextFactory(_dbOptions);
        _runService = new OrchestratorRunService(Serilog.Log.Logger);
        _transitionService = new WorkItemTransitionService(_dbFactory, NullLogger<WorkItemTransitionService>.Instance);
        _mockPendingWork.Setup(p => p.GetPendingJobsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<PendingJob>().AsReadOnly());
    }

    [Fact]
    public async Task DrainPendingItems_SuccessfulDispatch_SwapsLabelToInProgress()
    {
        // Arrange: insert a Pending WorkItem
        var workItemId = Guid.NewGuid();
        var request = new JobDistributionRequest
        {
            IssueIdentifier = "org/repo#42",
            IssueProviderConfigId = "issue-provider-1",
            RepoProviderConfigId = "repo-1",
            InitiatedBy = "loop",
            TaskType = WorkItemTaskType.Implementation,
            AgentSelector = "",
            RunId = workItemId.ToString(),
            TimeoutSeconds = 3600
        };
        var payload = JsonSerializer.Serialize(request, PipelineJsonOptions.Default);

        await using (var db = await _dbFactory.CreateDbContextAsync())
        {
            db.WorkItems.Add(new WorkItemEntity
            {
                Id = workItemId,
                TaskType = WorkItemTaskType.Implementation,
                IssueIdentifier = "org/repo#42",
                IssueProviderConfigId = "issue-provider-1",
                Status = WorkItemStatus.Pending,
                Payload = payload,
                AgentSelector = "",
                CreatedAt = DateTimeOffset.UtcNow,
                TimeoutSeconds = 3600
            });
            await db.SaveChangesAsync();
        }

        // Setup: idle agent available
        _mockResolver.Setup(r => r.ResolveAgent(""))
            .Returns(new AgentResolveResult("conn-1", "agent-1"));
        _mockAgentComm.Setup(c => c.AssignJobAsync("conn-1", It.IsAny<JobAssignmentMessage>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        // Use a completion signal to know when the label swap has been invoked
        var labelSwapCalled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _mockLabelSwapper
            .Setup(l => l.SwapLabelAsync("issue-provider-1", "org/repo#42", AgentLabels.InProgress, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask)
            .Callback(() => labelSwapCalled.TrySetResult());

        var service = CreateService();

        // Act: trigger a single drain cycle and wait for label swap (with timeout)
        service.Signal();
        var task = service.StartAsync(CancellationToken.None);
        var completed = await Task.WhenAny(labelSwapCalled.Task, Task.Delay(10_000));

        using var cts = new CancellationTokenSource();
        cts.Cancel();
        try { await service.StopAsync(cts.Token); } catch (OperationCanceledException) { }

        // Assert: label swap was actually called (not a timeout)
        completed.Should().BeSameAs(labelSwapCalled.Task, "label swap should have been called within timeout");
        _mockLabelSwapper.Verify(
            l => l.SwapLabelAsync("issue-provider-1", "org/repo#42", AgentLabels.InProgress, It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task DrainPendingItems_SignalRDeliveryFails_DoesNotSwapLabel()
    {
        // Arrange: insert a Pending WorkItem
        var workItemId = Guid.NewGuid();
        var request = new JobDistributionRequest
        {
            IssueIdentifier = "org/repo#99",
            IssueProviderConfigId = "issue-provider-1",
            RepoProviderConfigId = "repo-1",
            InitiatedBy = "loop",
            TaskType = WorkItemTaskType.Implementation,
            AgentSelector = "",
            RunId = workItemId.ToString(),
            TimeoutSeconds = 3600
        };
        var payload = JsonSerializer.Serialize(request, PipelineJsonOptions.Default);

        await using (var db = await _dbFactory.CreateDbContextAsync())
        {
            db.WorkItems.Add(new WorkItemEntity
            {
                Id = workItemId,
                TaskType = WorkItemTaskType.Implementation,
                IssueIdentifier = "org/repo#99",
                IssueProviderConfigId = "issue-provider-1",
                Status = WorkItemStatus.Pending,
                Payload = payload,
                AgentSelector = "",
                CreatedAt = DateTimeOffset.UtcNow,
                TimeoutSeconds = 3600
            });
            await db.SaveChangesAsync();
        }

        // Setup: agent available but SignalR delivery fails
        _mockResolver.Setup(r => r.ResolveAgent(""))
            .Returns(new AgentResolveResult("conn-1", "agent-1"));
        _mockAgentComm.Setup(c => c.AssignJobAsync("conn-1", It.IsAny<JobAssignmentMessage>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Connection lost"));

        var service = CreateService();

        // Act: trigger a single drain cycle
        await InvokeDrainAsync(service);

        // Assert: label was NOT swapped
        _mockLabelSwapper.Verify(
            l => l.SwapLabelAsync(It.IsAny<string>(), It.IsAny<string>(), AgentLabels.InProgress, It.IsAny<CancellationToken>()),
            Times.Never);

        // Assert: WorkItem was reverted to Pending (not stuck in Dispatched)
        await using var checkDb = await _dbFactory.CreateDbContextAsync();
        var item = await checkDb.WorkItems.FindAsync(workItemId);
        item.Should().NotBeNull();
        item!.Status.Should().Be(WorkItemStatus.Pending);
        item.DispatchedAt.Should().BeNull();
        item.AssignedAgentId.Should().BeNull();
    }

    [Fact]
    public async Task DrainPendingItems_NoIdleAgent_DoesNotSwapLabel()
    {
        // Arrange: insert a Pending WorkItem
        var workItemId = Guid.NewGuid();
        var request = new JobDistributionRequest
        {
            IssueIdentifier = "org/repo#77",
            IssueProviderConfigId = "issue-provider-1",
            RepoProviderConfigId = "repo-1",
            InitiatedBy = "loop",
            TaskType = WorkItemTaskType.Implementation,
            AgentSelector = "",
            RunId = workItemId.ToString(),
            TimeoutSeconds = 3600
        };
        var payload = JsonSerializer.Serialize(request, PipelineJsonOptions.Default);

        await using (var db = await _dbFactory.CreateDbContextAsync())
        {
            db.WorkItems.Add(new WorkItemEntity
            {
                Id = workItemId,
                TaskType = WorkItemTaskType.Implementation,
                IssueIdentifier = "org/repo#77",
                IssueProviderConfigId = "issue-provider-1",
                Status = WorkItemStatus.Pending,
                Payload = payload,
                AgentSelector = "",
                CreatedAt = DateTimeOffset.UtcNow,
                TimeoutSeconds = 3600
            });
            await db.SaveChangesAsync();
        }

        // Setup: no idle agent
        _mockResolver.Setup(r => r.ResolveAgent(""))
            .Returns((AgentResolveResult?)null);

        var service = CreateService();

        // Act: trigger a single drain cycle
        await InvokeDrainAsync(service);

        // Assert: label was NOT swapped
        _mockLabelSwapper.Verify(
            l => l.SwapLabelAsync(It.IsAny<string>(), It.IsAny<string>(), AgentLabels.InProgress, It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task DrainPendingItems_SignalRDeliveryFails_RevertsWorkItemToPending()
    {
        // Arrange: insert a Pending WorkItem
        var workItemId = Guid.NewGuid();
        var request = new JobDistributionRequest
        {
            IssueIdentifier = "org/repo#50",
            IssueProviderConfigId = "issue-provider-1",
            RepoProviderConfigId = "repo-1",
            InitiatedBy = "loop",
            TaskType = WorkItemTaskType.Implementation,
            AgentSelector = "",
            RunId = workItemId.ToString(),
            TimeoutSeconds = 3600
        };
        var payload = JsonSerializer.Serialize(request, PipelineJsonOptions.Default);

        await using (var db = await _dbFactory.CreateDbContextAsync())
        {
            db.WorkItems.Add(new WorkItemEntity
            {
                Id = workItemId,
                TaskType = WorkItemTaskType.Implementation,
                IssueIdentifier = "org/repo#50",
                IssueProviderConfigId = "issue-provider-1",
                Status = WorkItemStatus.Pending,
                Payload = payload,
                AgentSelector = "",
                CreatedAt = DateTimeOffset.UtcNow,
                TimeoutSeconds = 3600
            });
            await db.SaveChangesAsync();
        }

        // Setup: agent available but SignalR delivery fails
        _mockResolver.Setup(r => r.ResolveAgent(""))
            .Returns(new AgentResolveResult("conn-1", "agent-1"));
        _mockAgentComm.Setup(c => c.AssignJobAsync("conn-1", It.IsAny<JobAssignmentMessage>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Connection lost"));

        var service = CreateService();

        // Act: trigger a single drain cycle
        await InvokeDrainAsync(service);

        // Assert: WorkItem reverted to Pending with cleared dispatch fields
        await using var checkDb = await _dbFactory.CreateDbContextAsync();
        var item = await checkDb.WorkItems.FindAsync(workItemId);
        item.Should().NotBeNull();
        item!.Status.Should().Be(WorkItemStatus.Pending,
            "WorkItem must revert to Pending on SignalR delivery failure so it's eligible for re-drain");
        item.DispatchedAt.Should().BeNull("DispatchedAt must be cleared on revert");
        item.AssignedAgentId.Should().BeNull("AssignedAgentId must be cleared on revert");
        item.RetryCount.Should().Be(1, "RetryCount must be incremented on each failed delivery attempt");
    }

    [Fact]
    public async Task DrainPendingItems_RepeatedSignalRFailures_IncrementsRetryCount()
    {
        // Arrange: insert a Pending WorkItem with RetryCount = 0
        var workItemId = Guid.NewGuid();
        var request = new JobDistributionRequest
        {
            IssueIdentifier = "org/repo#60",
            IssueProviderConfigId = "issue-provider-1",
            RepoProviderConfigId = "repo-1",
            InitiatedBy = "loop",
            TaskType = WorkItemTaskType.Implementation,
            AgentSelector = "",
            RunId = workItemId.ToString(),
            TimeoutSeconds = 3600
        };
        var payload = JsonSerializer.Serialize(request, PipelineJsonOptions.Default);

        await using (var db = await _dbFactory.CreateDbContextAsync())
        {
            db.WorkItems.Add(new WorkItemEntity
            {
                Id = workItemId,
                TaskType = WorkItemTaskType.Implementation,
                IssueIdentifier = "org/repo#60",
                IssueProviderConfigId = "issue-provider-1",
                Status = WorkItemStatus.Pending,
                Payload = payload,
                AgentSelector = "",
                CreatedAt = DateTimeOffset.UtcNow,
                TimeoutSeconds = 3600,
                RetryCount = 0
            });
            await db.SaveChangesAsync();
        }

        // Setup: agent always available, SignalR delivery always fails
        _mockResolver.Setup(r => r.ResolveAgent(""))
            .Returns(new AgentResolveResult("conn-1", "agent-1"));
        _mockAgentComm.Setup(c => c.AssignJobAsync("conn-1", It.IsAny<JobAssignmentMessage>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Connection lost"));

        // Act: first drain cycle
        var service1 = CreateService();
        await InvokeDrainAsync(service1);

        // Assert: RetryCount is 1 after first failure
        await using (var db1 = await _dbFactory.CreateDbContextAsync())
        {
            var item1 = await db1.WorkItems.FindAsync(workItemId);
            item1!.RetryCount.Should().Be(1);
            item1.Status.Should().Be(WorkItemStatus.Pending);
        }

        // Act: second drain cycle (new service instance — same DB and mocks)
        var service2 = CreateService();
        await InvokeDrainAsync(service2);

        // Assert: RetryCount is 2 after second failure
        await using var db2 = await _dbFactory.CreateDbContextAsync();
        var item2 = await db2.WorkItems.FindAsync(workItemId);
        item2.Should().NotBeNull();
        item2!.RetryCount.Should().Be(2,
            "RetryCount must increment on each failed delivery attempt");
        item2.Status.Should().Be(WorkItemStatus.Pending,
            "WorkItem must remain Pending after repeated failures (not Failed)");
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
            _mockProjectStore.Object);
    }

    /// <summary>
    /// Invokes the internal DrainPendingItemsAsync method via the BackgroundService's
    /// ExecuteAsync using a short-lived cancellation token.
    /// PendingWorkItemDrainService is a BackgroundService so we start it and let one cycle run.
    /// Uses a generous timeout to accommodate slow CI runners.
    /// </summary>
    private static async Task InvokeDrainAsync(PendingWorkItemDrainService service)
    {
        using var cts = new CancellationTokenSource();
        // Signal the service to wake up immediately
        service.Signal();
        // Start the service and let it run one cycle then cancel
        var task = service.StartAsync(cts.Token);
        // Give it enough time to process — needs to be generous for slow CI runners (ARM, etc.)
        await Task.Delay(3000);
        cts.Cancel();
        try { await task; } catch (OperationCanceledException) { }
        await service.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task DrainPendingItems_TransitionsToDispatched_BeforeSendingViaSIgnalR()
    {
        // Reproduction: DrainService called AssignJobAsync BEFORE TransitionAsync(Dispatched).
        // This caused the agent's JobAccepted → "Pending → Running" transition to fail
        // because the DB row was still Status=Pending when the agent reported acceptance.
        //
        // The fix: TransitionAsync(Dispatched) must run BEFORE AssignJobAsync so the
        // DB state is Dispatched by the time the agent receives and accepts the job.
        var workItemId = Guid.NewGuid();
        var request = new JobDistributionRequest
        {
            IssueIdentifier = "org/repo#100",
            IssueProviderConfigId = "issue-provider-1",
            RepoProviderConfigId = "repo-1",
            InitiatedBy = "loop",
            TaskType = WorkItemTaskType.Implementation,
            AgentSelector = "",
            RunId = workItemId.ToString(),
            TimeoutSeconds = 3600
        };
        var payload = JsonSerializer.Serialize(request, PipelineJsonOptions.Default);

        await using (var db = await _dbFactory.CreateDbContextAsync())
        {
            db.WorkItems.Add(new WorkItemEntity
            {
                Id = workItemId,
                TaskType = WorkItemTaskType.Implementation,
                IssueIdentifier = "org/repo#100",
                IssueProviderConfigId = "issue-provider-1",
                Status = WorkItemStatus.Pending,
                Payload = payload,
                AgentSelector = "",
                CreatedAt = DateTimeOffset.UtcNow,
                TimeoutSeconds = 3600
            });
            await db.SaveChangesAsync();
        }

        // Track the order of operations
        var operationOrder = new List<string>();

        _mockResolver.Setup(r => r.ResolveAgent(""))
            .Returns(new AgentResolveResult("conn-1", "agent-1"));
        _mockAgentComm.Setup(c => c.AssignJobAsync("conn-1", It.IsAny<JobAssignmentMessage>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask)
            .Callback(() => operationOrder.Add("AssignJobAsync"));

        var service = CreateService();

        // Act: invoke drain — need to check DB state WHEN AssignJobAsync is called
        WorkItemStatus? statusAtAssignTime = null;
        _mockAgentComm.Setup(c => c.AssignJobAsync("conn-1", It.IsAny<JobAssignmentMessage>(), It.IsAny<CancellationToken>()))
            .Returns(async () =>
            {
                // At the moment AssignJobAsync is called, check what the DB status is
                await using var checkDb = await _dbFactory.CreateDbContextAsync();
                var item = await checkDb.WorkItems.FindAsync(workItemId);
                statusAtAssignTime = item?.Status;
            });

        await InvokeDrainAsync(service);

        // Assert: the WorkItem must already be Dispatched when AssignJobAsync fires
        statusAtAssignTime.Should().Be(WorkItemStatus.Dispatched,
            "WorkItem must be transitioned to Dispatched BEFORE sending via SignalR, " +
            "otherwise the agent's JobAccepted → Running transition fails with 'Invalid transition: Pending → Running'");
    }

    [Fact]
    public async Task DrainPendingItems_WithProjectId_InjectsProjectSecrets()
    {
        // Reproduction: DrainService dispatched jobs WITHOUT project secrets.
        // SignalRWorkDistributor injected them at delivery time, but the drain path
        // skipped this step entirely. Agents running jobs dispatched via drain had
        // no access to project-level secrets (API keys, tokens).
        var workItemId = Guid.NewGuid();
        var request = new JobDistributionRequest
        {
            IssueIdentifier = "org/repo#200",
            IssueProviderConfigId = "issue-provider-1",
            RepoProviderConfigId = "repo-1",
            InitiatedBy = "loop",
            TaskType = WorkItemTaskType.Implementation,
            AgentSelector = "",
            RunId = workItemId.ToString(),
            TimeoutSeconds = 3600,
            ProjectId = "project-42"
        };
        var payload = JsonSerializer.Serialize(request, PipelineJsonOptions.Default);

        await using (var db = await _dbFactory.CreateDbContextAsync())
        {
            db.WorkItems.Add(new WorkItemEntity
            {
                Id = workItemId,
                TaskType = WorkItemTaskType.Implementation,
                IssueIdentifier = "org/repo#200",
                IssueProviderConfigId = "issue-provider-1",
                Status = WorkItemStatus.Pending,
                Payload = payload,
                AgentSelector = "",
                CreatedAt = DateTimeOffset.UtcNow,
                TimeoutSeconds = 3600,
                ProjectId = "project-42"
            });
            await db.SaveChangesAsync();
        }

        _mockResolver.Setup(r => r.ResolveAgent(""))
            .Returns(new AgentResolveResult("conn-1", "agent-1"));

        // Setup: project store returns secrets for project-42
        var secrets = new Dictionary<string, string> { ["API_KEY"] = "secret-value-123" };
        _mockProjectStore.Setup(p => p.GetProjectByIdAsync("project-42", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PipelineProject { Id = "project-42", Name = "Test", Secrets = secrets });

        // Capture the message sent to AssignJobAsync
        JobAssignmentMessage? capturedMessage = null;
        _mockAgentComm.Setup(c => c.AssignJobAsync("conn-1", It.IsAny<JobAssignmentMessage>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask)
            .Callback<string, JobAssignmentMessage, CancellationToken>((_, msg, _) => capturedMessage = msg);

        var service = CreateService();
        await InvokeDrainAsync(service);

        // Assert: the message must contain project secrets
        capturedMessage.Should().NotBeNull("AssignJobAsync should have been called");
        capturedMessage!.ProjectSecrets.Should().NotBeNull("project secrets must be injected");
        capturedMessage.ProjectSecrets.Should().ContainKey("API_KEY");
        capturedMessage.ProjectSecrets!["API_KEY"].Should().Be("secret-value-123");
    }

    public void Dispose()
    {
        // Cleanup in-memory database
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
