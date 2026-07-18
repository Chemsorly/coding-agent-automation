using System.Reflection;
using System.Text.Json;
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

namespace CodingAgentWebUI.Pipeline.UnitTests.Services;

/// <summary>
/// Verifies that PendingWorkItemDrainService creates a PipelineRun when the
/// original run is no longer in memory (orchestrator restart scenario).
/// </summary>
public class PendingWorkItemDrainServiceRunCreationTests : IDisposable
{
    private readonly DbContextOptions<PipelineDbContext> _dbOptions;
    private readonly TestDbContextFactory _dbFactory;
    private readonly WorkItemTransitionService _transitionService;
    private readonly Mock<ISignalRWorkDistributorAgentResolver> _mockAgentResolver;
    private readonly Mock<IAgentCommunication> _mockAgentComm;
    private readonly Mock<IOrchestratorRunService> _mockRunService;
    private readonly Mock<IPendingWorkQuery> _mockPendingWorkQuery;
    private readonly Mock<IProjectStore> _mockProjectStore;
    private readonly Mock<ILabelService> _mockLabelService;

    public PendingWorkItemDrainServiceRunCreationTests()
    {
        var dbName = $"DrainRunCreation-{Guid.NewGuid()}";
        _dbOptions = new DbContextOptionsBuilder<PipelineDbContext>()
            .UseInMemoryDatabase(dbName)
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;

        using (var ctx = new TestPipelineDbContext(_dbOptions))
            ctx.Database.EnsureCreated();

        _dbFactory = new TestDbContextFactory(_dbOptions);
        _transitionService = new WorkItemTransitionService(_dbFactory, NullLogger<WorkItemTransitionService>.Instance);

        _mockAgentResolver = new Mock<ISignalRWorkDistributorAgentResolver>();
        _mockAgentComm = new Mock<IAgentCommunication>();
        _mockRunService = new Mock<IOrchestratorRunService>();
        _mockPendingWorkQuery = new Mock<IPendingWorkQuery>();
        _mockProjectStore = new Mock<IProjectStore>();
        _mockLabelService = new Mock<ILabelService>();

        _mockPendingWorkQuery
            .Setup(q => q.GetPendingJobsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<PendingJob>());
    }

    public void Dispose()
    {
        using var db = new TestPipelineDbContext(_dbOptions);
        db.Database.EnsureDeleted();
    }

    [Fact]
    public async Task DrainPendingItems_WhenRunNotInMemory_CreatesNewPipelineRun()
    {
        // Arrange
        var runId = Guid.NewGuid().ToString();
        var agentId = "agent-42";
        var connectionId = "conn-abc";

        var request = new JobDistributionRequest
        {
            IssueIdentifier = "owner/repo#99",
            IssueProviderConfigId = "issue-provider-1",
            RepoProviderConfigId = "repo-provider-1",
            InitiatedBy = "loop",
            TaskType = WorkItemTaskType.Implementation,
            AgentSelector = "",
            TimeoutSeconds = 1800,
            RunId = runId,
            RunType = PipelineRunType.Implementation,
            IssueDetail = new IssueDetail
            {
                Identifier = "owner/repo#99",
                Title = "Fix the widget",
                Description = "Widget is broken",
                Labels = []
            }
        };

        var payload = JsonSerializer.Serialize(request, PipelineJsonOptions.Default);
        await InsertWorkItem(Guid.NewGuid(), "owner/repo#99", "", WorkItemStatus.Pending, payload);

        // Agent resolver returns an agent
        _mockAgentResolver
            .Setup(r => r.ResolveAgent(""))
            .Returns(new AgentResolveResult(connectionId, agentId));

        // GetRun returns null — simulating orchestrator restart
        _mockRunService.Setup(r => r.GetRun(runId)).Returns((PipelineRun?)null);

        // SignalR delivery succeeds
        _mockAgentComm
            .Setup(c => c.AssignJobAsync(connectionId, It.IsAny<JobAssignmentMessage>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var service = CreateService();

        // Act
        await InvokeDrainPendingItems(service);

        // Assert — AddRun must have been called with a run matching the request data
        _mockRunService.Verify(r => r.AddRun(It.Is<PipelineRun>(run =>
            run.RunId == runId &&
            run.IssueIdentifier == "owner/repo#99" &&
            run.IssueProviderConfigId == "issue-provider-1" &&
            run.RepoProviderConfigId == "repo-provider-1" &&
            run.InitiatedBy == "loop" &&
            run.RunType == PipelineRunType.Implementation &&
            run.AgentId == agentId &&
            run.IssueTitle == "Fix the widget"
        )), Times.Once);
    }

    [Fact]
    public async Task DrainPendingItems_WhenRunExistsInMemory_DoesNotCallAddRun()
    {
        // Arrange
        var runId = Guid.NewGuid().ToString();
        var agentId = "agent-42";
        var connectionId = "conn-abc";

        var existingRun = PipelineRun.Create(
            runId: runId,
            issueIdentifier: "owner/repo#10",
            issueTitle: "Existing",
            issueProviderConfigId: "ip-1",
            repoProviderConfigId: "rp-1");

        var request = new JobDistributionRequest
        {
            IssueIdentifier = "owner/repo#10",
            IssueProviderConfigId = "ip-1",
            RepoProviderConfigId = "rp-1",
            InitiatedBy = "manual",
            TaskType = WorkItemTaskType.Implementation,
            AgentSelector = "",
            TimeoutSeconds = 1800,
            RunId = runId,
            RunType = PipelineRunType.Implementation
        };

        var payload = JsonSerializer.Serialize(request, PipelineJsonOptions.Default);
        await InsertWorkItem(Guid.NewGuid(), "owner/repo#10", "", WorkItemStatus.Pending, payload);

        _mockAgentResolver
            .Setup(r => r.ResolveAgent(""))
            .Returns(new AgentResolveResult(connectionId, agentId));

        // GetRun returns the existing run
        _mockRunService.Setup(r => r.GetRun(runId)).Returns(existingRun);

        _mockAgentComm
            .Setup(c => c.AssignJobAsync(connectionId, It.IsAny<JobAssignmentMessage>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var service = CreateService();

        // Act
        await InvokeDrainPendingItems(service);

        // Assert — AddRun must NOT be called; existing run should just get AgentId set
        _mockRunService.Verify(r => r.AddRun(It.IsAny<PipelineRun>()), Times.Never);
        existingRun.AgentId.Should().Be(agentId);
    }

    [Fact]
    public async Task DrainPendingItems_WhenRunIdIsNull_DoesNotCallAddRun()
    {
        // Arrange — request with no RunId
        var agentId = "agent-42";
        var connectionId = "conn-abc";

        var request = new JobDistributionRequest
        {
            IssueIdentifier = "owner/repo#5",
            IssueProviderConfigId = "ip-1",
            RepoProviderConfigId = "rp-1",
            InitiatedBy = "manual",
            TaskType = WorkItemTaskType.Implementation,
            AgentSelector = "",
            TimeoutSeconds = 1800,
            RunId = null, // No RunId
            RunType = PipelineRunType.Implementation
        };

        var payload = JsonSerializer.Serialize(request, PipelineJsonOptions.Default);
        await InsertWorkItem(Guid.NewGuid(), "owner/repo#5", "", WorkItemStatus.Pending, payload);

        _mockAgentResolver
            .Setup(r => r.ResolveAgent(""))
            .Returns(new AgentResolveResult(connectionId, agentId));

        _mockAgentComm
            .Setup(c => c.AssignJobAsync(connectionId, It.IsAny<JobAssignmentMessage>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var service = CreateService();

        // Act
        await InvokeDrainPendingItems(service);

        // Assert — no run operations
        _mockRunService.Verify(r => r.GetRun(It.IsAny<RunId>()), Times.Never);
        _mockRunService.Verify(r => r.AddRun(It.IsAny<PipelineRun>()), Times.Never);
    }

    // ── Helpers ──

    private PendingWorkItemDrainService CreateService()
    {
        return new PendingWorkItemDrainService(
            _dbFactory,
            _mockAgentResolver.Object,
            _mockAgentComm.Object,
            _mockRunService.Object,
            _transitionService,
            _mockPendingWorkQuery.Object,
            _mockLabelService.Object,
            NullLogger<PendingWorkItemDrainService>.Instance);
    }

    private static async Task InvokeDrainPendingItems(PendingWorkItemDrainService service)
    {
        // DrainPendingItemsAsync is private — invoke via reflection
        var method = typeof(PendingWorkItemDrainService).GetMethod(
            "DrainPendingItemsAsync",
            BindingFlags.NonPublic | BindingFlags.Instance);

        var task = (Task)method!.Invoke(service, [CancellationToken.None])!;
        await task;
    }

    private async Task InsertWorkItem(Guid id, string issueId, string selector,
        WorkItemStatus status, string? payload = null)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        db.WorkItems.Add(new WorkItemEntity
        {
            Id = id,
            IssueIdentifier = issueId,
            IssueProviderConfigId = "provider-1",
            Status = status,
            AgentSelector = selector,
            CreatedAt = DateTimeOffset.UtcNow,
            TimeoutSeconds = 1800,
            Payload = payload ?? "{}"
        });
        await db.SaveChangesAsync();
    }

    private sealed class TestPipelineDbContext : PipelineDbContext
    {
        public TestPipelineDbContext(DbContextOptions<PipelineDbContext> options) : base(options) { }
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);
            foreach (var et in modelBuilder.Model.GetEntityTypes())
            {
                var rv = et.FindProperty("RowVersion");
                if (rv != null) { rv.IsConcurrencyToken = false; rv.ValueGenerated = Microsoft.EntityFrameworkCore.Metadata.ValueGenerated.Never; }
            }
            foreach (var et in modelBuilder.Model.GetEntityTypes())
                foreach (var idx in et.GetIndexes().Where(i => i.GetFilter() != null).ToList())
                    et.RemoveIndex(idx);
        }
    }

    private sealed class TestDbContextFactory : IDbContextFactory<PipelineDbContext>
    {
        private readonly DbContextOptions<PipelineDbContext> _options;
        public TestDbContextFactory(DbContextOptions<PipelineDbContext> options) => _options = options;
        public PipelineDbContext CreateDbContext() => new TestPipelineDbContext(_options);
        public Task<PipelineDbContext> CreateDbContextAsync(CancellationToken ct = default) => Task.FromResult(CreateDbContext());
    }
}
