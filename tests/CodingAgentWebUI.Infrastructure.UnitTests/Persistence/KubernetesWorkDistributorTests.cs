using AwesomeAssertions;
using CodingAgentWebUI.Api.Client;
using CodingAgentWebUI.Infrastructure.Persistence;
using CodingAgentWebUI.Infrastructure.Persistence.Entities;
using CodingAgentWebUI.Infrastructure.Persistence.Services;
using CodingAgentWebUI.Orchestration.Dispatch;
using CodingAgentWebUI.Pipeline.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace CodingAgentWebUI.Infrastructure.UnitTests.Persistence;

/// <summary>
/// Unit tests for KubernetesWorkDistributor.
/// Validates: Requirements 4.6 (IsIssueDistributed), 4.8 (crash-resilient persistence),
/// and Req 5.2 (DistributeAsync uses IPipelineApiWorkItemClient.CreateAsync).
/// </summary>
public class KubernetesWorkDistributorTests : IDisposable
{
    private readonly DbContextOptions<PipelineDbContext> _dbOptions;
    private readonly InMemoryDbContextFactory _dbFactory;
    private readonly Mock<IPipelineApiWorkItemClient> _mockApiClient;
    private readonly KubernetesWorkDistributor _distributor;

    public KubernetesWorkDistributorTests()
    {
        var dbName = $"K8sWorkDistributorTests-{Guid.NewGuid()}";
        _dbOptions = new DbContextOptionsBuilder<PipelineDbContext>()
            .UseInMemoryDatabase(dbName)
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;

        using (var ctx = new InMemoryPipelineDbContext(_dbOptions))
        {
            ctx.Database.EnsureCreated();
        }

        _dbFactory = new InMemoryDbContextFactory(_dbOptions);
        var transitionService = new WorkItemTransitionService(
            _dbFactory, NullLogger<WorkItemTransitionService>.Instance);

        _mockApiClient = new Mock<IPipelineApiWorkItemClient>();
        _mockApiClient
            .Setup(c => c.CreateAsync(It.IsAny<JobDistributionRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => Guid.NewGuid());

        _distributor = new KubernetesWorkDistributor(
            _mockApiClient.Object,
            _dbFactory,
            transitionService,
            NullLogger<KubernetesWorkDistributor>.Instance);
    }

    public void Dispose()
    {
        using var db = new InMemoryPipelineDbContext(_dbOptions);
        db.Database.EnsureDeleted();
    }

    // ── DistributeAsync — API-backed (Req 5.2) ──────────────────────────

    [Fact]
    public async Task DistributeAsync_CallsApiClientCreateAsync()
    {
        var request = CreateRequest("owner/repo#1", "provider-1");

        await _distributor.DistributeAsync(request, CancellationToken.None);

        _mockApiClient.Verify(
            c => c.CreateAsync(
                It.Is<JobDistributionRequest>(r => r.IssueIdentifier == request.IssueIdentifier),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task DistributeAsync_ReturnsSuccessWithWorkItemId()
    {
        var expectedId = Guid.NewGuid();
        _mockApiClient
            .Setup(c => c.CreateAsync(It.IsAny<JobDistributionRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(expectedId);

        var request = CreateRequest("owner/repo#2", "provider-2");
        var result = await _distributor.DistributeAsync(request, CancellationToken.None);

        result.Success.Should().BeTrue();
        result.Queued.Should().BeTrue();
        result.WorkItemId.Should().Be(expectedId.ToString());
        result.ErrorMessage.Should().BeNull();
    }

    [Fact]
    public async Task DistributeAsync_WhenApiThrows_ReturnsFailureResult()
    {
        _mockApiClient
            .Setup(c => c.CreateAsync(It.IsAny<JobDistributionRequest>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("Pipeline API unreachable"));

        var request = CreateRequest("owner/repo#3", "provider-3");
        var result = await _distributor.DistributeAsync(request, CancellationToken.None);

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("Pipeline API unreachable");
    }

    [Fact]
    public async Task DistributeAsync_DoesNotInsertIntoDB()
    {
        var request = CreateRequest("owner/repo#4", "provider-4");
        await _distributor.DistributeAsync(request, CancellationToken.None);

        // API-backed: no row should appear in local DB
        await using var db = await _dbFactory.CreateDbContextAsync();
        var count = await db.WorkItems.CountAsync();
        count.Should().Be(0, "DistributeAsync now creates WorkItems via the Pipeline API, not local DB");
    }

    // ── CancelJobAsync ──────────────────────────────────────────────────

    [Fact]
    public async Task CancelJobAsync_PendingItem_TransitionsToCancelled()
    {
        // Insert row directly (DistributeAsync no longer inserts locally)
        var workItemId = await InsertPendingWorkItemAsync("owner/repo#cancel-1", "provider-c1");

        var cancelled = await _distributor.CancelJobAsync(workItemId.ToString(), CancellationToken.None);

        cancelled.Should().BeTrue();
        await using var db = await _dbFactory.CreateDbContextAsync();
        var item = await db.WorkItems.FirstAsync(w => w.Id == workItemId);
        item.Status.Should().Be(WorkItemStatus.Cancelled);
    }

    [Fact]
    public async Task CancelJobAsync_InvalidGuid_ReturnsFalse()
    {
        var cancelled = await _distributor.CancelJobAsync("not-a-guid", CancellationToken.None);
        cancelled.Should().BeFalse();
    }

    [Fact]
    public async Task CancelJobAsync_NonExistentId_ReturnsFalse()
    {
        var cancelled = await _distributor.CancelJobAsync(Guid.NewGuid().ToString(), CancellationToken.None);
        cancelled.Should().BeFalse();
    }

    // ── GetJobStatusAsync ───────────────────────────────────────────────

    [Fact]
    public async Task GetJobStatusAsync_ExistingPendingItem_ReturnsPending()
    {
        var workItemId = await InsertPendingWorkItemAsync("owner/repo#status-1", "provider-s1");

        var status = await _distributor.GetJobStatusAsync(workItemId.ToString(), CancellationToken.None);

        status.Should().Be(JobDistributionStatus.Pending);
    }

    [Fact]
    public async Task GetJobStatusAsync_NonExistentId_ReturnsUnknown()
    {
        var status = await _distributor.GetJobStatusAsync(Guid.NewGuid().ToString(), CancellationToken.None);
        status.Should().Be(JobDistributionStatus.Unknown);
    }

    [Fact]
    public async Task GetJobStatusAsync_InvalidGuid_ReturnsUnknown()
    {
        var status = await _distributor.GetJobStatusAsync("invalid", CancellationToken.None);
        status.Should().Be(JobDistributionStatus.Unknown);
    }

    [Fact]
    public async Task GetJobStatusAsync_CancelledItem_ReturnsCancelled()
    {
        var workItemId = await InsertPendingWorkItemAsync("owner/repo#status-2", "provider-s2");
        await _distributor.CancelJobAsync(workItemId.ToString(), CancellationToken.None);

        var status = await _distributor.GetJobStatusAsync(workItemId.ToString(), CancellationToken.None);

        status.Should().Be(JobDistributionStatus.Cancelled);
    }

    // ── IsIssueDistributedAsync ─────────────────────────────────────────

    [Fact]
    public async Task IsIssueDistributedAsync_PendingItem_ReturnsTrue()
    {
        await InsertWorkItemAsync("owner/repo#7", "provider-7", WorkItemStatus.Pending);

        var distributed = await _distributor.IsIssueDistributedAsync(
            "owner/repo#7", "provider-7", CancellationToken.None);

        distributed.Should().BeTrue();
    }

    [Fact]
    public async Task IsIssueDistributedAsync_CancelledItem_WithinCooldown_ReturnsTrue()
    {
        var workItemId = await InsertPendingWorkItemAsync("owner/repo#8", "provider-8");
        await _distributor.CancelJobAsync(workItemId.ToString(), CancellationToken.None);

        var distributed = await _distributor.IsIssueDistributedAsync(
            "owner/repo#8", "provider-8", CancellationToken.None);

        distributed.Should().BeTrue("recently-cancelled items within restart dedup cooldown are treated as distributed");
    }

    [Fact]
    public async Task IsIssueDistributedAsync_NoMatchingItem_ReturnsFalse()
    {
        var distributed = await _distributor.IsIssueDistributedAsync(
            "nonexistent", "provider-x", CancellationToken.None);

        distributed.Should().BeFalse();
    }

    [Fact]
    public async Task IsIssueDistributedAsync_DispatchedItem_ReturnsTrue()
    {
        await InsertWorkItemAsync("owner/repo#9", "provider-9", WorkItemStatus.Dispatched);

        var distributed = await _distributor.IsIssueDistributedAsync(
            "owner/repo#9", "provider-9", CancellationToken.None);

        distributed.Should().BeTrue();
    }

    [Fact]
    public async Task IsIssueDistributedAsync_RunningItem_ReturnsTrue()
    {
        await InsertWorkItemAsync("owner/repo#10", "provider-10", WorkItemStatus.Running);

        var distributed = await _distributor.IsIssueDistributedAsync(
            "owner/repo#10", "provider-10", CancellationToken.None);

        distributed.Should().BeTrue();
    }

    [Fact]
    public async Task IsIssueDistributedAsync_SucceededItem_ReturnsFalse()
    {
        await InsertWorkItemAsync("owner/repo#11", "provider-11", WorkItemStatus.Succeeded);

        var distributed = await _distributor.IsIssueDistributedAsync(
            "owner/repo#11", "provider-11", CancellationToken.None);

        distributed.Should().BeFalse();
    }

    [Fact]
    public async Task IsIssueDistributedAsync_FailedItem_ReturnsFalse()
    {
        await InsertWorkItemAsync("owner/repo#12", "provider-12", WorkItemStatus.Failed);

        var distributed = await _distributor.IsIssueDistributedAsync(
            "owner/repo#12", "provider-12", CancellationToken.None);

        distributed.Should().BeFalse();
    }

    // ── GetActiveIssueIdentifiersAsync ──────────────────────────────────

    [Fact]
    public async Task GetActiveIssueIdentifiersAsync_ReturnsOnlyNonTerminalPairs()
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        db.WorkItems.AddRange(
            new WorkItemEntity { Id = Guid.NewGuid(), IssueIdentifier = "active-1", IssueProviderConfigId = "p1", Status = WorkItemStatus.Pending, CreatedAt = DateTimeOffset.UtcNow, AgentSelector = "kiro", TimeoutSeconds = 1800 },
            new WorkItemEntity { Id = Guid.NewGuid(), IssueIdentifier = "active-2", IssueProviderConfigId = "p2", Status = WorkItemStatus.Dispatched, CreatedAt = DateTimeOffset.UtcNow, AgentSelector = "kiro", TimeoutSeconds = 1800 },
            new WorkItemEntity { Id = Guid.NewGuid(), IssueIdentifier = "active-3", IssueProviderConfigId = "p3", Status = WorkItemStatus.Running, CreatedAt = DateTimeOffset.UtcNow, AgentSelector = "kiro", TimeoutSeconds = 1800 },
            new WorkItemEntity { Id = Guid.NewGuid(), IssueIdentifier = "done-1", IssueProviderConfigId = "p4", Status = WorkItemStatus.Succeeded, CreatedAt = DateTimeOffset.UtcNow, AgentSelector = "kiro", TimeoutSeconds = 1800 },
            new WorkItemEntity { Id = Guid.NewGuid(), IssueIdentifier = "done-2", IssueProviderConfigId = "p5", Status = WorkItemStatus.Failed, CreatedAt = DateTimeOffset.UtcNow, AgentSelector = "kiro", TimeoutSeconds = 1800 },
            new WorkItemEntity { Id = Guid.NewGuid(), IssueIdentifier = "done-3", IssueProviderConfigId = "p6", Status = WorkItemStatus.Cancelled, CreatedAt = DateTimeOffset.UtcNow, AgentSelector = "kiro", TimeoutSeconds = 1800 }
        );
        await db.SaveChangesAsync();

        var active = await _distributor.GetActiveIssueIdentifiersAsync(CancellationToken.None);

        active.Should().HaveCount(3);
        active.Should().Contain(("active-1", "p1"));
        active.Should().Contain(("active-2", "p2"));
        active.Should().Contain(("active-3", "p3"));
        active.Should().NotContain(("done-1", "p4"));
        active.Should().NotContain(("done-2", "p5"));
        active.Should().NotContain(("done-3", "p6"));
    }

    [Fact]
    public async Task GetActiveIssueIdentifiersAsync_EmptyDb_ReturnsEmptySet()
    {
        var active = await _distributor.GetActiveIssueIdentifiersAsync(CancellationToken.None);
        active.Should().BeEmpty();
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    private static JobDistributionRequest CreateRequest(string issueId, string providerId) => new()
    {
        IssueIdentifier = issueId,
        IssueProviderConfigId = providerId,
        RepoProviderConfigId = "repo-provider-1",
        InitiatedBy = "pipeline-loop",
        TaskType = WorkItemTaskType.Implementation,
        AgentSelector = "kiro,linux",
        TimeoutSeconds = 1800,
        ProjectId = "proj-1",
        RunType = PipelineRunType.Implementation
    };

    private async Task<Guid> InsertPendingWorkItemAsync(string issueId, string providerId)
        => await InsertWorkItemAsync(issueId, providerId, WorkItemStatus.Pending);

    private async Task<Guid> InsertWorkItemAsync(string issueId, string providerId, WorkItemStatus status)
    {
        var id = Guid.NewGuid();
        await using var db = await _dbFactory.CreateDbContextAsync();
        db.WorkItems.Add(new WorkItemEntity
        {
            Id = id,
            IssueIdentifier = issueId,
            IssueProviderConfigId = providerId,
            Status = status,
            CreatedAt = DateTimeOffset.UtcNow,
            AgentSelector = "kiro",
            TimeoutSeconds = 1800
        });
        await db.SaveChangesAsync();
        return id;
    }

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
