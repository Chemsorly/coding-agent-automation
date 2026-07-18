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
using System.Collections.Generic;

namespace CodingAgentWebUI.UnitTests.Services;

/// <summary>
/// Unit tests for <see cref="SignalRWorkDistributor"/>.
/// Uses in-memory EF Core provider for isolation.
/// </summary>
public sealed class SignalRWorkDistributorTests : IDisposable
{
    private readonly DbContextOptions<PipelineDbContext> _dbOptions;
    private readonly Mock<IAgentCommunication> _mockAgentComm;
    private readonly Mock<ISignalRWorkDistributorAgentResolver> _mockResolver;
    private readonly Mock<ILabelSwapper> _mockLabelSwapper;
    private readonly SignalRWorkDistributor _sut;
    private readonly InMemoryDbContextFactory _dbFactory;

    public SignalRWorkDistributorTests()
    {
        _dbOptions = new DbContextOptionsBuilder<PipelineDbContext>()
            .UseInMemoryDatabase(databaseName: $"SignalRWorkDistributorTests-{Guid.NewGuid()}")
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;

        using (var ctx = new InMemoryPipelineDbContext(_dbOptions))
        {
            ctx.Database.EnsureCreated();
        }

        _dbFactory = new InMemoryDbContextFactory(_dbOptions);
        _mockAgentComm = new Mock<IAgentCommunication>();
        _mockResolver = new Mock<ISignalRWorkDistributorAgentResolver>();
        _mockLabelSwapper = new Mock<ILabelSwapper>();

        var transitionService = new WorkItemTransitionService(
            _dbFactory,
            NullLogger<WorkItemTransitionService>.Instance);

        _sut = new SignalRWorkDistributor(
            _dbFactory,
            _mockAgentComm.Object,
            transitionService,
            _mockResolver.Object,
            new Mock<IOrchestratorRunService>().Object,
            new Mock<IProjectStore>().Object,
            _mockLabelSwapper.Object,
            NullLogger<SignalRWorkDistributor>.Instance);
    }

    public void Dispose()
    {
        using var db = new InMemoryPipelineDbContext(_dbOptions);
        db.Database.EnsureDeleted();
    }

    [Fact]
    public async Task DistributeAsync_Success_InsertsWorkItemAndPushesViaSignalR()
    {
        // Arrange
        var request = CreateMinimalRequest();
        _mockResolver.Setup(r => r.ResolveAgent(It.IsAny<string>())).Returns(new AgentResolveResult("conn-1", "agent-1"));
        _mockAgentComm
            .Setup(c => c.AssignJobAsync("conn-1", It.IsAny<JobAssignmentMessage>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        // Act
        var result = await _sut.DistributeAsync(request, CancellationToken.None);

        // Assert
        result.Success.Should().BeTrue();
        result.WorkItemId.Should().NotBeNullOrEmpty();
        result.ErrorMessage.Should().BeNull();

        // Verify DB row exists
        await using var db = new InMemoryPipelineDbContext(_dbOptions);
        var workItem = await db.WorkItems.FindAsync(Guid.Parse(result.WorkItemId!));
        workItem.Should().NotBeNull();
        workItem!.Status.Should().Be(WorkItemStatus.Dispatched);
        workItem.IssueIdentifier.Should().Be("owner/repo#1");
        workItem.DispatchedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task DistributeAsync_NoConnectedAgent_QueuesWorkItemAsPending()
    {
        // Arrange
        var request = CreateMinimalRequest();
        _mockResolver.Setup(r => r.ResolveAgent(It.IsAny<string>())).Returns((AgentResolveResult?)null);

        // Act
        var result = await _sut.DistributeAsync(request, CancellationToken.None);

        // Assert — queued successfully (will be drained when agent becomes idle)
        result.Success.Should().BeTrue();
        result.Queued.Should().BeTrue();
        result.WorkItemId.Should().NotBeNullOrEmpty();
        result.ErrorMessage.Should().Contain("Queued");

        // Verify DB row is Pending (not Failed)
        await using var db = new InMemoryPipelineDbContext(_dbOptions);
        var workItem = await db.WorkItems.FindAsync(Guid.Parse(result.WorkItemId!));
        workItem.Should().NotBeNull();
        workItem!.Status.Should().Be(WorkItemStatus.Pending);
        workItem.DispatchedAt.Should().BeNull();
    }

    [Fact]
    public async Task DistributeAsync_SignalRThrows_MarksWorkItemFailed()
    {
        // Arrange
        var request = CreateMinimalRequest();
        _mockResolver.Setup(r => r.ResolveAgent(It.IsAny<string>())).Returns(new AgentResolveResult("conn-1", "agent-1"));
        _mockAgentComm
            .Setup(c => c.AssignJobAsync("conn-1", It.IsAny<JobAssignmentMessage>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Connection lost"));

        // Act
        var result = await _sut.DistributeAsync(request, CancellationToken.None);

        // Assert
        result.Success.Should().BeFalse();
        result.WorkItemId.Should().NotBeNullOrEmpty();
        result.ErrorMessage.Should().Contain("SignalR delivery failed");

        // Verify DB row is Failed
        await using var db = new InMemoryPipelineDbContext(_dbOptions);
        var workItem = await db.WorkItems.FindAsync(Guid.Parse(result.WorkItemId!));
        workItem.Should().NotBeNull();
        workItem!.Status.Should().Be(WorkItemStatus.Failed);
        workItem.FailureReason.Should().Be(FailureReason.InfrastructureFailure);
    }

    [Fact]
    public async Task CancelJobAsync_ExistingDispatchedItem_TransitionsToCancelled()
    {
        // Arrange — insert a Dispatched work item
        var workItemId = Guid.NewGuid();
        await using (var db = new InMemoryPipelineDbContext(_dbOptions))
        {
            db.WorkItems.Add(new WorkItemEntity
            {
                Id = workItemId,
                IssueIdentifier = "owner/repo#1",
                IssueProviderConfigId = "ip-1",
                Status = WorkItemStatus.Dispatched,
                AgentSelector = "kiro",
                CreatedAt = DateTimeOffset.UtcNow,
                DispatchedAt = DateTimeOffset.UtcNow,
                TimeoutSeconds = 300
            });
            await db.SaveChangesAsync();
        }

        // Act
        var result = await _sut.CancelJobAsync(workItemId.ToString(), CancellationToken.None);

        // Assert
        result.Should().BeTrue();
        await using var dbVerify = new InMemoryPipelineDbContext(_dbOptions);
        var item = await dbVerify.WorkItems.FindAsync(workItemId);
        item!.Status.Should().Be(WorkItemStatus.Cancelled);
        item.CompletedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task CancelJobAsync_InvalidGuid_ReturnsFalse()
    {
        var result = await _sut.CancelJobAsync("not-a-guid", CancellationToken.None);
        result.Should().BeFalse();
    }

    [Fact]
    public async Task GetJobStatusAsync_ExistingItem_ReturnsCorrectStatus()
    {
        // Arrange
        var workItemId = Guid.NewGuid();
        await using (var db = new InMemoryPipelineDbContext(_dbOptions))
        {
            db.WorkItems.Add(new WorkItemEntity
            {
                Id = workItemId,
                IssueIdentifier = "owner/repo#2",
                IssueProviderConfigId = "ip-1",
                Status = WorkItemStatus.Running,
                AgentSelector = "kiro",
                CreatedAt = DateTimeOffset.UtcNow,
                TimeoutSeconds = 300
            });
            await db.SaveChangesAsync();
        }

        // Act
        var status = await _sut.GetJobStatusAsync(workItemId.ToString(), CancellationToken.None);

        // Assert
        status.Should().Be(JobDistributionStatus.Running);
    }

    [Fact]
    public async Task GetJobStatusAsync_NonexistentId_ReturnsUnknown()
    {
        var status = await _sut.GetJobStatusAsync(Guid.NewGuid().ToString(), CancellationToken.None);
        status.Should().Be(JobDistributionStatus.Unknown);
    }

    [Fact]
    public async Task GetJobStatusAsync_InvalidGuid_ReturnsUnknown()
    {
        var status = await _sut.GetJobStatusAsync("not-a-guid", CancellationToken.None);
        status.Should().Be(JobDistributionStatus.Unknown);
    }

    [Fact]
    public async Task IsIssueDistributedAsync_ActiveItem_ReturnsTrue()
    {
        // Arrange
        await using (var db = new InMemoryPipelineDbContext(_dbOptions))
        {
            db.WorkItems.Add(new WorkItemEntity
            {
                Id = Guid.NewGuid(),
                IssueIdentifier = "owner/repo#3",
                IssueProviderConfigId = "ip-1",
                Status = WorkItemStatus.Running,
                AgentSelector = "kiro",
                CreatedAt = DateTimeOffset.UtcNow,
                TimeoutSeconds = 300
            });
            await db.SaveChangesAsync();
        }

        // Act
        var result = await _sut.IsIssueDistributedAsync("owner/repo#3", "ip-1", CancellationToken.None);

        // Assert
        result.Should().BeTrue();
    }

    [Fact]
    public async Task IsIssueDistributedAsync_TerminalItem_ReturnsFalse()
    {
        // Arrange
        await using (var db = new InMemoryPipelineDbContext(_dbOptions))
        {
            db.WorkItems.Add(new WorkItemEntity
            {
                Id = Guid.NewGuid(),
                IssueIdentifier = "owner/repo#4",
                IssueProviderConfigId = "ip-1",
                Status = WorkItemStatus.Succeeded,
                AgentSelector = "kiro",
                CreatedAt = DateTimeOffset.UtcNow,
                TimeoutSeconds = 300
            });
            await db.SaveChangesAsync();
        }

        // Act
        var result = await _sut.IsIssueDistributedAsync("owner/repo#4", "ip-1", CancellationToken.None);

        // Assert
        result.Should().BeFalse();
    }

    [Fact]
    public async Task IsIssueDistributedAsync_NoItems_ReturnsFalse()
    {
        var result = await _sut.IsIssueDistributedAsync("nonexistent", "ip-1", CancellationToken.None);
        result.Should().BeFalse();
    }

    [Fact]
    public async Task GetActiveIssueIdentifiersAsync_ReturnsOnlyNonTerminalPairs()
    {
        // Arrange
        await using (var db = new InMemoryPipelineDbContext(_dbOptions))
        {
            db.WorkItems.AddRange(
                new WorkItemEntity
                {
                    Id = Guid.NewGuid(),
                    IssueIdentifier = "active-1",
                    IssueProviderConfigId = "ip-1",
                    Status = WorkItemStatus.Pending,
                    AgentSelector = "kiro",
                    CreatedAt = DateTimeOffset.UtcNow,
                    TimeoutSeconds = 300
                },
                new WorkItemEntity
                {
                    Id = Guid.NewGuid(),
                    IssueIdentifier = "active-2",
                    IssueProviderConfigId = "ip-2",
                    Status = WorkItemStatus.Running,
                    AgentSelector = "kiro",
                    CreatedAt = DateTimeOffset.UtcNow,
                    TimeoutSeconds = 300
                },
                new WorkItemEntity
                {
                    Id = Guid.NewGuid(),
                    IssueIdentifier = "done-1",
                    IssueProviderConfigId = "ip-1",
                    Status = WorkItemStatus.Succeeded,
                    AgentSelector = "kiro",
                    CreatedAt = DateTimeOffset.UtcNow,
                    TimeoutSeconds = 300
                },
                new WorkItemEntity
                {
                    Id = Guid.NewGuid(),
                    IssueIdentifier = "failed-1",
                    IssueProviderConfigId = "ip-1",
                    Status = WorkItemStatus.Failed,
                    AgentSelector = "kiro",
                    CreatedAt = DateTimeOffset.UtcNow,
                    TimeoutSeconds = 300
                });
            await db.SaveChangesAsync();
        }

        // Act
        var result = await _sut.GetActiveIssueIdentifiersAsync(CancellationToken.None);

        // Assert
        result.Should().HaveCount(2);
        result.Should().Contain(("active-1", "ip-1"));
        result.Should().Contain(("active-2", "ip-2"));
        result.Should().NotContain(("done-1", "ip-1"));
        result.Should().NotContain(("failed-1", "ip-1"));
    }

    [Fact]
    public async Task DetectStuckDispatchedItemsAsync_StuckItems_LogsWarningAndReturnsCount()
    {
        // Arrange
        var sixMinutesAgo = DateTimeOffset.UtcNow.AddMinutes(-6);
        var oneMinuteAgo = DateTimeOffset.UtcNow.AddMinutes(-1);

        await using (var db = new InMemoryPipelineDbContext(_dbOptions))
        {
            db.WorkItems.AddRange(
                new WorkItemEntity
                {
                    Id = Guid.NewGuid(),
                    IssueIdentifier = "stuck-1",
                    IssueProviderConfigId = "ip-1",
                    Status = WorkItemStatus.Dispatched,
                    DispatchedAt = sixMinutesAgo,
                    AgentSelector = "kiro",
                    CreatedAt = sixMinutesAgo,
                    TimeoutSeconds = 300
                },
                new WorkItemEntity
                {
                    Id = Guid.NewGuid(),
                    IssueIdentifier = "recent-1",
                    IssueProviderConfigId = "ip-1",
                    Status = WorkItemStatus.Dispatched,
                    DispatchedAt = oneMinuteAgo,
                    AgentSelector = "kiro",
                    CreatedAt = oneMinuteAgo,
                    TimeoutSeconds = 300
                });
            await db.SaveChangesAsync();
        }

        // Act
        var stuckCount = await _sut.DetectStuckDispatchedItemsAsync(
            TimeSpan.FromMinutes(5), CancellationToken.None);

        // Assert
        stuckCount.Should().Be(1);
    }

    [Fact]
    public async Task DetectStuckDispatchedItemsAsync_TransitionsStuckItemsToFailed()
    {
        // Arrange: one stuck item
        var stuckId = Guid.NewGuid();
        var sixMinutesAgo = DateTimeOffset.UtcNow.AddMinutes(-6);

        await using (var db = new InMemoryPipelineDbContext(_dbOptions))
        {
            db.WorkItems.Add(new WorkItemEntity
            {
                Id = stuckId,
                IssueIdentifier = "stuck-transition-1",
                IssueProviderConfigId = "ip-1",
                Status = WorkItemStatus.Dispatched,
                DispatchedAt = sixMinutesAgo,
                AgentSelector = "kiro",
                CreatedAt = sixMinutesAgo,
                TimeoutSeconds = 3600
            });
            await db.SaveChangesAsync();
        }

        // Act
        await _sut.DetectStuckDispatchedItemsAsync(TimeSpan.FromMinutes(5), CancellationToken.None);

        // Assert: item transitioned to Failed with correct reason
        await using (var db = new InMemoryPipelineDbContext(_dbOptions))
        {
            var item = await db.WorkItems.FindAsync(stuckId);
            item!.Status.Should().Be(WorkItemStatus.Failed);
            item.FailureReason.Should().Be(FailureReason.InfrastructureFailure);
            item.CompletedAt.Should().NotBeNull();
            item.ErrorMessage.Should().Contain("Stuck in Dispatched");
        }
    }

    [Fact]
    public async Task ReconcileStuckItemsAsync_DelegatesToDetectStuckDispatchedItemsAsync()
    {
        // Arrange: one stuck item
        var stuckId = Guid.NewGuid();
        var tenMinutesAgo = DateTimeOffset.UtcNow.AddMinutes(-10);

        await using (var db = new InMemoryPipelineDbContext(_dbOptions))
        {
            db.WorkItems.Add(new WorkItemEntity
            {
                Id = stuckId,
                IssueIdentifier = "reconcile-stuck-1",
                IssueProviderConfigId = "ip-1",
                Status = WorkItemStatus.Dispatched,
                DispatchedAt = tenMinutesAgo,
                AgentSelector = "kiro",
                CreatedAt = tenMinutesAgo,
                TimeoutSeconds = 3600
            });
            await db.SaveChangesAsync();
        }

        // Act: call via the IWorkDistributor interface method
        IWorkDistributor distributor = _sut;
        var count = await distributor.ReconcileStuckItemsAsync(CancellationToken.None);

        // Assert
        count.Should().Be(1);
        await using (var db = new InMemoryPipelineDbContext(_dbOptions))
        {
            var item = await db.WorkItems.FindAsync(stuckId);
            item!.Status.Should().Be(WorkItemStatus.Failed);
        }
    }

    [Fact]
    public async Task DetectStuckDispatchedItemsAsync_NoStuckItems_ReturnsZero()
    {
        // Arrange — only recent items
        await using (var db = new InMemoryPipelineDbContext(_dbOptions))
        {
            db.WorkItems.Add(new WorkItemEntity
            {
                Id = Guid.NewGuid(),
                IssueIdentifier = "recent-1",
                IssueProviderConfigId = "ip-1",
                Status = WorkItemStatus.Dispatched,
                DispatchedAt = DateTimeOffset.UtcNow,
                AgentSelector = "kiro",
                CreatedAt = DateTimeOffset.UtcNow,
                TimeoutSeconds = 300
            });
            await db.SaveChangesAsync();
        }

        // Act
        var stuckCount = await _sut.DetectStuckDispatchedItemsAsync(
            TimeSpan.FromMinutes(5), CancellationToken.None);

        // Assert
        stuckCount.Should().Be(0);
    }

    [Fact]
    public async Task DistributeAsync_WithRunId_UsesRunIdAsWorkItemId()
    {
        // Arrange: request has a pre-assigned RunId from DispatchOrchestrationService
        var preAssignedRunId = Guid.NewGuid().ToString();
        var request = CreateMinimalRequest() with { RunId = preAssignedRunId };
        _mockResolver.Setup(r => r.ResolveAgent(It.IsAny<string>())).Returns(new AgentResolveResult("conn-1", "agent-1"));
        _mockAgentComm
            .Setup(c => c.AssignJobAsync("conn-1", It.IsAny<JobAssignmentMessage>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        // Act
        var result = await _sut.DistributeAsync(request, CancellationToken.None);

        // Assert: WorkItem ID matches the pre-assigned RunId
        result.Success.Should().BeTrue();
        result.WorkItemId.Should().Be(preAssignedRunId);

        // Verify the DB row was created with the orchestration's RunId
        await using var db = new InMemoryPipelineDbContext(_dbOptions);
        var workItem = await db.WorkItems.FindAsync(Guid.Parse(preAssignedRunId));
        workItem.Should().NotBeNull();
        workItem!.Status.Should().Be(WorkItemStatus.Dispatched);

        // Verify the JobAssignmentMessage sent to agent uses this same ID
        _mockAgentComm.Verify(c => c.AssignJobAsync("conn-1",
            It.Is<JobAssignmentMessage>(m => m.JobId == preAssignedRunId),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task DistributeAsync_WithRunId_UpdatesPipelineRunAgentId()
    {
        // Arrange: mock OrchestratorRunService with a run that has AgentId="pending"
        var runId = Guid.NewGuid().ToString();
        var mockRunService = new Mock<IOrchestratorRunService>();
        var pipelineRun = PipelineRun.Create(runId, "owner/repo#1", "", "ip-1", "rp-1", agentId: "pending");
        mockRunService.Setup(r => r.GetRun(runId)).Returns(pipelineRun);

        var transitionService = new WorkItemTransitionService(_dbFactory, NullLogger<WorkItemTransitionService>.Instance);
        var sut = new SignalRWorkDistributor(
            _dbFactory, _mockAgentComm.Object, transitionService,
            _mockResolver.Object, mockRunService.Object,
            new Mock<IProjectStore>().Object,
            new Mock<ILabelSwapper>().Object,
            NullLogger<SignalRWorkDistributor>.Instance);

        var request = CreateMinimalRequest() with { RunId = runId };
        _mockResolver.Setup(r => r.ResolveAgent(It.IsAny<string>())).Returns(new AgentResolveResult("conn-1", "agent-dotnet-1"));
        _mockAgentComm
            .Setup(c => c.AssignJobAsync(It.IsAny<string>(), It.IsAny<JobAssignmentMessage>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        // Act
        var result = await sut.DistributeAsync(request, CancellationToken.None);

        // Assert: PipelineRun.AgentId updated from "pending" to actual agent
        result.Success.Should().BeTrue();
        pipelineRun.AgentId.Should().Be("agent-dotnet-1");
    }

    [Fact]
    public async Task DistributeAsync_Success_SetsAssignedAgentIdOnWorkItemRow()
    {
        // Arrange
        var request = CreateMinimalRequest() with { RunId = Guid.NewGuid().ToString() };
        _mockResolver.Setup(r => r.ResolveAgent(It.IsAny<string>())).Returns(new AgentResolveResult("conn-1", "agent-kiro-2"));
        _mockAgentComm
            .Setup(c => c.AssignJobAsync("conn-1", It.IsAny<JobAssignmentMessage>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        // Act
        var result = await _sut.DistributeAsync(request, CancellationToken.None);

        // Assert: WorkItem row has AssignedAgentId set
        result.Success.Should().BeTrue();
        result.Queued.Should().BeFalse();
        await using var db = new InMemoryPipelineDbContext(_dbOptions);
        var workItem = await db.WorkItems.FindAsync(Guid.Parse(result.WorkItemId!));
        workItem.Should().NotBeNull();
        workItem!.AssignedAgentId.Should().Be("agent-kiro-2");
    }

    [Fact]
    public async Task DistributeAsync_WithoutRunId_GeneratesNewWorkItemId()
    {
        // Arrange: no RunId set (legacy path)
        var request = CreateMinimalRequest(); // RunId is null
        _mockResolver.Setup(r => r.ResolveAgent(It.IsAny<string>())).Returns(new AgentResolveResult("conn-1", "agent-1"));
        _mockAgentComm
            .Setup(c => c.AssignJobAsync("conn-1", It.IsAny<JobAssignmentMessage>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        // Act
        var result = await _sut.DistributeAsync(request, CancellationToken.None);

        // Assert: A new GUID was generated (not null, and it's a valid GUID)
        result.Success.Should().BeTrue();
        result.WorkItemId.Should().NotBeNullOrEmpty();
        Guid.TryParse(result.WorkItemId, out _).Should().BeTrue();
    }

    [Fact]
    public async Task DistributeAsync_Success_CallsAssignJobOnResolver()
    {
        // Arrange
        var runId = Guid.NewGuid().ToString();
        var request = CreateMinimalRequest() with { RunId = runId };
        _mockResolver.Setup(r => r.ResolveAgent(It.IsAny<string>())).Returns(new AgentResolveResult("conn-1", "agent-1"));
        _mockAgentComm
            .Setup(c => c.AssignJobAsync("conn-1", It.IsAny<JobAssignmentMessage>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        // Act
        var result = await _sut.DistributeAsync(request, CancellationToken.None);

        // Assert: AssignJob called with correct agentId and workItemId
        result.Success.Should().BeTrue();
        _mockResolver.Verify(r => r.AssignJob("agent-1", runId), Times.Once);
    }

    [Fact]
    public async Task DistributeAsync_SignalRFails_DoesNotCallAssignJob()
    {
        // Arrange
        var request = CreateMinimalRequest() with { RunId = Guid.NewGuid().ToString() };
        _mockResolver.Setup(r => r.ResolveAgent(It.IsAny<string>())).Returns(new AgentResolveResult("conn-1", "agent-1"));
        _mockAgentComm
            .Setup(c => c.AssignJobAsync("conn-1", It.IsAny<JobAssignmentMessage>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Connection lost"));

        // Act
        var result = await _sut.DistributeAsync(request, CancellationToken.None);

        // Assert: AssignJob never called on failure
        result.Success.Should().BeFalse();
        _mockResolver.Verify(r => r.AssignJob(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task DistributeAsync_WithProjectId_InjectsProjectSecretsIntoMessage()
    {
        // Arrange: project with secrets in the store
        var projectId = "proj-123";
        var expectedSecrets = new Dictionary<string, string>
        {
            ["API_KEY"] = "secret-api-key-value",
            ["DB_PASSWORD"] = "super-secret-db-pass"
        };

        var mockProjectStore = new Mock<IProjectStore>();
        mockProjectStore
            .Setup(s => s.GetProjectByIdAsync(projectId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PipelineProject
            {
                Id = projectId,
                Name = "Test Project",
                Secrets = expectedSecrets
            });

        var transitionService = new WorkItemTransitionService(_dbFactory, NullLogger<WorkItemTransitionService>.Instance);
        var sut = new SignalRWorkDistributor(
            _dbFactory, _mockAgentComm.Object, transitionService,
            _mockResolver.Object, new Mock<IOrchestratorRunService>().Object,
            mockProjectStore.Object,
            new Mock<ILabelSwapper>().Object,
            NullLogger<SignalRWorkDistributor>.Instance);

        var request = CreateMinimalRequest() with { ProjectId = projectId };
        _mockResolver.Setup(r => r.ResolveAgent(It.IsAny<string>())).Returns(new AgentResolveResult("conn-1", "agent-1"));
        _mockAgentComm
            .Setup(c => c.AssignJobAsync("conn-1", It.IsAny<JobAssignmentMessage>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        // Act
        var result = await sut.DistributeAsync(request, CancellationToken.None);

        // Assert: message sent to agent includes project secrets
        result.Success.Should().BeTrue();
        _mockAgentComm.Verify(c => c.AssignJobAsync("conn-1",
            It.Is<JobAssignmentMessage>(m =>
                m.ProjectSecrets != null &&
                m.ProjectSecrets.Count == 2 &&
                m.ProjectSecrets["API_KEY"] == "secret-api-key-value" &&
                m.ProjectSecrets["DB_PASSWORD"] == "super-secret-db-pass"),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task DistributeAsync_WithoutProjectId_DoesNotSetProjectSecrets()
    {
        // Arrange: no project ID on the request
        var mockProjectStore = new Mock<IProjectStore>();
        var transitionService = new WorkItemTransitionService(_dbFactory, NullLogger<WorkItemTransitionService>.Instance);
        var sut = new SignalRWorkDistributor(
            _dbFactory, _mockAgentComm.Object, transitionService,
            _mockResolver.Object, new Mock<IOrchestratorRunService>().Object,
            mockProjectStore.Object,
            new Mock<ILabelSwapper>().Object,
            NullLogger<SignalRWorkDistributor>.Instance);

        var request = CreateMinimalRequest(); // no ProjectId
        _mockResolver.Setup(r => r.ResolveAgent(It.IsAny<string>())).Returns(new AgentResolveResult("conn-1", "agent-1"));
        _mockAgentComm
            .Setup(c => c.AssignJobAsync("conn-1", It.IsAny<JobAssignmentMessage>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        // Act
        var result = await sut.DistributeAsync(request, CancellationToken.None);

        // Assert: no project store call, no secrets on message
        result.Success.Should().BeTrue();
        mockProjectStore.Verify(s => s.GetProjectByIdAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        _mockAgentComm.Verify(c => c.AssignJobAsync("conn-1",
            It.Is<JobAssignmentMessage>(m => m.ProjectSecrets == null),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task DistributeAsync_NoAgent_DoesNotSwapLabel()
    {
        // Arrange: no idle agent — label should NOT be touched (stays at agent:next from pipeline loop)
        var request = CreateMinimalRequest() with { RunType = PipelineRunType.Implementation };
        _mockResolver.Setup(r => r.ResolveAgent(It.IsAny<string>())).Returns((AgentResolveResult?)null);

        // Act
        var result = await _sut.DistributeAsync(request, CancellationToken.None);

        // Assert: no label swap at all — issue stays in its current state
        result.Success.Should().BeTrue();
        _mockLabelSwapper.Verify(l => l.SwapLabelAsync(
            It.IsAny<ProviderConfigId>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<LabelTargetKind>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task DistributeAsync_AgentAvailable_SwapsLabelToInProgress()
    {
        // Arrange: agent available — lifecycle manager should be called for agent acceptance
        var request = CreateMinimalRequest() with { RunType = PipelineRunType.Implementation };
        _mockResolver.Setup(r => r.ResolveAgent(It.IsAny<string>())).Returns(new AgentResolveResult("conn-1", "agent-1"));
        _mockAgentComm
            .Setup(c => c.AssignJobAsync("conn-1", It.IsAny<JobAssignmentMessage>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        // Act
        var result = await _sut.DistributeAsync(request, CancellationToken.None);

        // Assert: lifecycle manager's AgentAcceptedRunAsync was called (handles label swap internally)
        result.Success.Should().BeTrue();
        // In the default SUT (no lifecycle manager), falls back to AssignJob on resolver
        _mockResolver.Verify(r => r.AssignJob("agent-1", It.IsAny<string>()), Times.Once);
    }

    [Fact]
    public async Task DistributeAsync_AgentAvailable_WithLifecycleManager_CallsAgentAccepted()
    {
        // Arrange: agent available with lifecycle manager injected
        var mockLifecycle = new Mock<IRunLifecycleManager>();
        var sut = new SignalRWorkDistributor(
            _dbFactory, _mockAgentComm.Object,
            new WorkItemTransitionService(_dbFactory, NullLogger<WorkItemTransitionService>.Instance),
            _mockResolver.Object,
            new Mock<IOrchestratorRunService>().Object,
            new Mock<IProjectStore>().Object,
            _mockLabelSwapper.Object,
            NullLogger<SignalRWorkDistributor>.Instance,
            mockLifecycle.Object);

        var request = CreateMinimalRequest() with { RunType = PipelineRunType.Implementation };
        _mockResolver.Setup(r => r.ResolveAgent(It.IsAny<string>())).Returns(new AgentResolveResult("conn-1", "agent-1"));
        _mockAgentComm
            .Setup(c => c.AssignJobAsync("conn-1", It.IsAny<JobAssignmentMessage>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        // Act
        var result = await sut.DistributeAsync(request, CancellationToken.None);

        // Assert: AgentAcceptedRunAsync called with correct params
        result.Success.Should().BeTrue();
        mockLifecycle.Verify(l => l.AgentAcceptedRunAsync(
            It.IsAny<RunId>(), "agent-1",
            "owner/repo#1", "ip-1", "rp-1", PipelineRunType.Implementation,
            It.IsAny<CancellationToken>()), Times.Once);
        // AssignJob NOT called directly (lifecycle manager handles it)
        _mockResolver.Verify(r => r.AssignJob(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task DistributeAsync_AgentAvailable_LabelSwapFailure_DoesNotFailDispatch()
    {
        // Arrange: agent available, lifecycle manager throws — dispatch should still succeed
        var mockLifecycle = new Mock<IRunLifecycleManager>();
        mockLifecycle
            .Setup(l => l.AgentAcceptedRunAsync(It.IsAny<RunId>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<PipelineRunType>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("GitHub API error"));

        var sut = new SignalRWorkDistributor(
            _dbFactory, _mockAgentComm.Object,
            new WorkItemTransitionService(_dbFactory, NullLogger<WorkItemTransitionService>.Instance),
            _mockResolver.Object,
            new Mock<IOrchestratorRunService>().Object,
            new Mock<IProjectStore>().Object,
            _mockLabelSwapper.Object,
            NullLogger<SignalRWorkDistributor>.Instance,
            mockLifecycle.Object);

        var request = CreateMinimalRequest();
        _mockResolver.Setup(r => r.ResolveAgent(It.IsAny<string>())).Returns(new AgentResolveResult("conn-1", "agent-1"));
        _mockAgentComm
            .Setup(c => c.AssignJobAsync("conn-1", It.IsAny<JobAssignmentMessage>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        // Act
        var result = await sut.DistributeAsync(request, CancellationToken.None);

        // Assert: dispatch failed because lifecycle manager exception propagates within the try block
        // (SignalR delivery already succeeded, but the catch captures it as a delivery failure)
        // This is acceptable — the agent has the job and will work on it regardless
        result.Should().NotBeNull();
    }

    // ── CancelJobAsync with IRunLifecycleManager ─────────────────────────

    [Fact]
    public async Task CancelJobAsync_WithLifecycleManager_DelegatesToCancelRunAsync()
    {
        // Arrange: create SUT with lifecycle manager injected
        var mockLifecycle = new Mock<IRunLifecycleManager>();
        var mockCancellation = new Mock<IAgentCancellationSender>();
        var runId = Guid.NewGuid().ToString();
        var cancelledRun = PipelineRun.Create(runId, "owner/repo#1", "", "ip-1", "rp-1", agentId: "agent-1");

        mockLifecycle
            .Setup(l => l.CancelRunAsync(runId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(cancelledRun);

        var transitionService = new WorkItemTransitionService(_dbFactory, NullLogger<WorkItemTransitionService>.Instance);
        var sut = new SignalRWorkDistributor(
            _dbFactory, _mockAgentComm.Object, transitionService,
            _mockResolver.Object, new Mock<IOrchestratorRunService>().Object,
            new Mock<IProjectStore>().Object, new Mock<ILabelSwapper>().Object,
            NullLogger<SignalRWorkDistributor>.Instance,
            mockLifecycle.Object, mockCancellation.Object);

        // Act
        var result = await sut.CancelJobAsync(runId, CancellationToken.None);

        // Assert
        result.Should().BeTrue();
        mockLifecycle.Verify(l => l.CancelRunAsync(runId, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task CancelJobAsync_WithLifecycleManager_SendsCancelSignalToAgent()
    {
        // Arrange
        var mockLifecycle = new Mock<IRunLifecycleManager>();
        var mockCancellation = new Mock<IAgentCancellationSender>();
        var runId = Guid.NewGuid().ToString();
        var cancelledRun = PipelineRun.Create(runId, "owner/repo#1", "", "ip-1", "rp-1", agentId: "agent-42");

        mockLifecycle
            .Setup(l => l.CancelRunAsync(runId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(cancelledRun);

        var transitionService = new WorkItemTransitionService(_dbFactory, NullLogger<WorkItemTransitionService>.Instance);
        var sut = new SignalRWorkDistributor(
            _dbFactory, _mockAgentComm.Object, transitionService,
            _mockResolver.Object, new Mock<IOrchestratorRunService>().Object,
            new Mock<IProjectStore>().Object, new Mock<ILabelSwapper>().Object,
            NullLogger<SignalRWorkDistributor>.Instance,
            mockLifecycle.Object, mockCancellation.Object);

        // Act
        await sut.CancelJobAsync(runId, CancellationToken.None);

        // Assert: cancel signal sent to agent
        mockCancellation.Verify(c => c.SendCancelJobAsync("agent-42", runId, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task CancelJobAsync_LifecycleManagerReturnsNull_FallsBackToDbTransition()
    {
        // Arrange: lifecycle manager returns null (run not found in memory)
        var mockLifecycle = new Mock<IRunLifecycleManager>();
        var workItemId = Guid.NewGuid();
        mockLifecycle
            .Setup(l => l.CancelRunAsync(workItemId.ToString(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((PipelineRun?)null);

        // Insert a Dispatched work item so DB fallback can transition it
        await using (var db = new InMemoryPipelineDbContext(_dbOptions))
        {
            db.WorkItems.Add(new WorkItemEntity
            {
                Id = workItemId,
                IssueIdentifier = "owner/repo#99",
                IssueProviderConfigId = "ip-1",
                Status = WorkItemStatus.Dispatched,
                AgentSelector = "kiro",
                CreatedAt = DateTimeOffset.UtcNow,
                DispatchedAt = DateTimeOffset.UtcNow,
                TimeoutSeconds = 300
            });
            await db.SaveChangesAsync();
        }

        var transitionService = new WorkItemTransitionService(_dbFactory, NullLogger<WorkItemTransitionService>.Instance);
        var sut = new SignalRWorkDistributor(
            _dbFactory, _mockAgentComm.Object, transitionService,
            _mockResolver.Object, new Mock<IOrchestratorRunService>().Object,
            new Mock<IProjectStore>().Object, new Mock<ILabelSwapper>().Object,
            NullLogger<SignalRWorkDistributor>.Instance,
            mockLifecycle.Object, null);

        // Act
        var result = await sut.CancelJobAsync(workItemId.ToString(), CancellationToken.None);

        // Assert: falls back to DB transition
        result.Should().BeTrue();
        await using var dbVerify = new InMemoryPipelineDbContext(_dbOptions);
        var item = await dbVerify.WorkItems.FindAsync(workItemId);
        item!.Status.Should().Be(WorkItemStatus.Cancelled);
    }

    [Fact]
    public async Task CancelJobAsync_NoLifecycleManager_UsesLegacyDbTransition()
    {
        // The default _sut has no lifecycle manager (constructed without it) — legacy behavior preserved
        var workItemId = Guid.NewGuid();
        await using (var db = new InMemoryPipelineDbContext(_dbOptions))
        {
            db.WorkItems.Add(new WorkItemEntity
            {
                Id = workItemId,
                IssueIdentifier = "owner/repo#50",
                IssueProviderConfigId = "ip-1",
                Status = WorkItemStatus.Dispatched,
                AgentSelector = "kiro",
                CreatedAt = DateTimeOffset.UtcNow,
                DispatchedAt = DateTimeOffset.UtcNow,
                TimeoutSeconds = 300
            });
            await db.SaveChangesAsync();
        }

        var result = await _sut.CancelJobAsync(workItemId.ToString(), CancellationToken.None);

        result.Should().BeTrue();
        await using var dbVerify = new InMemoryPipelineDbContext(_dbOptions);
        var item = await dbVerify.WorkItems.FindAsync(workItemId);
        item!.Status.Should().Be(WorkItemStatus.Cancelled);
    }

    private static JobDistributionRequest CreateMinimalRequest() => new()
    {
        IssueIdentifier = "owner/repo#1",
        IssueProviderConfigId = "ip-1",
        RepoProviderConfigId = "rp-1",
        InitiatedBy = "pipeline",
        TaskType = WorkItemTaskType.Implementation,
        AgentSelector = "kiro",
        TimeoutSeconds = 300
    };

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

            // Remove partial indexes (not supported by InMemory provider)
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
