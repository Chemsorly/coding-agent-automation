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

        var service = CreateService();

        // Act: trigger a single drain cycle
        await InvokeDrainAsync(service);

        // Assert: label was swapped to in-progress
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
            NullLogger<PendingWorkItemDrainService>.Instance);
    }

    /// <summary>
    /// Invokes the internal DrainPendingItemsAsync method via the BackgroundService's
    /// ExecuteAsync using a short-lived cancellation token.
    /// PendingWorkItemDrainService is a BackgroundService so we start it and let one cycle run.
    /// </summary>
    private static async Task InvokeDrainAsync(PendingWorkItemDrainService service)
    {
        using var cts = new CancellationTokenSource();
        // Signal the service to wake up immediately
        service.Signal();
        // Start the service and let it run one cycle then cancel
        var task = service.StartAsync(cts.Token);
        // TODO: Replace Task.Delay with a completion signal (e.g., TaskCompletionSource) to avoid
        //       timing-dependent flakiness on slow CI machines (#997 review)
        // Give it time to process
        await Task.Delay(500);
        cts.Cancel();
        try { await task; } catch (OperationCanceledException) { }
        await service.StopAsync(CancellationToken.None);
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
