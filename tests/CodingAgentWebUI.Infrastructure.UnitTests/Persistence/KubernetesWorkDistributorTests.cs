using AwesomeAssertions;
using CodingAgentWebUI.Infrastructure.Persistence;
using CodingAgentWebUI.Infrastructure.Persistence.Entities;
using CodingAgentWebUI.Infrastructure.Persistence.Services;
using CodingAgentWebUI.Orchestration.Dispatch;
using CodingAgentWebUI.Pipeline.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CodingAgentWebUI.Infrastructure.UnitTests.Persistence;

/// <summary>
/// Unit tests for KubernetesWorkDistributor.
/// Validates: Requirements 4.4 (insert Pending), 4.6 (IsIssueDistributed), 4.8 (crash-resilient persistence).
/// </summary>
public class KubernetesWorkDistributorTests : IDisposable
{
    private readonly DbContextOptions<PipelineDbContext> _dbOptions;
    private readonly InMemoryDbContextFactory _dbFactory;
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
        _distributor = new KubernetesWorkDistributor(
            _dbFactory, transitionService, NullLogger<KubernetesWorkDistributor>.Instance);
    }

    public void Dispose()
    {
        using var db = new InMemoryPipelineDbContext(_dbOptions);
        db.Database.EnsureDeleted();
    }

    // ── DistributeAsync ─────────────────────────────────────────────────

    [Fact]
    public async Task DistributeAsync_InsertsWorkItemWithPendingStatus()
    {
        var request = CreateRequest("owner/repo#1", "provider-1");

        var result = await _distributor.DistributeAsync(request, CancellationToken.None);

        result.Success.Should().BeTrue();
        result.WorkItemId.Should().NotBeNullOrEmpty();
        result.ErrorMessage.Should().BeNull();

        // Verify row in DB
        await using var db = await _dbFactory.CreateDbContextAsync();
        var item = await db.WorkItems.FirstOrDefaultAsync(w => w.Id == Guid.Parse(result.WorkItemId!));
        item.Should().NotBeNull();
        item!.Status.Should().Be(WorkItemStatus.Pending);
        item.IssueIdentifier.Should().Be("owner/repo#1");
        item.IssueProviderConfigId.Should().Be("provider-1");
        item.AgentSelector.Should().Be("kiro,linux");
        item.TimeoutSeconds.Should().Be(1800);
        item.TaskType.Should().Be(WorkItemTaskType.Implementation);
        item.ProjectId.Should().Be("proj-1");
        item.Payload.Should().NotBeNull();
    }

    [Fact]
    public async Task DistributeAsync_SerializesPayloadAsJsonb()
    {
        var request = CreateRequest("owner/repo#2", "provider-2");

        var result = await _distributor.DistributeAsync(request, CancellationToken.None);

        await using var db = await _dbFactory.CreateDbContextAsync();
        var item = await db.WorkItems.FirstAsync(w => w.Id == Guid.Parse(result.WorkItemId!));

        // Payload should contain the serialized request
        var rawJson = item.Payload!.RootElement.GetRawText();
        rawJson.Should().Contain("owner/repo#2");
        rawJson.Should().Contain("provider-2");
    }

    [Fact]
    public async Task DistributeAsync_SetsCreatedAtToCurrentTime()
    {
        var before = DateTimeOffset.UtcNow;
        var request = CreateRequest("owner/repo#3", "provider-3");

        var result = await _distributor.DistributeAsync(request, CancellationToken.None);

        var after = DateTimeOffset.UtcNow;
        await using var db = await _dbFactory.CreateDbContextAsync();
        var item = await db.WorkItems.FirstAsync(w => w.Id == Guid.Parse(result.WorkItemId!));

        item.CreatedAt.Should().BeOnOrAfter(before);
        item.CreatedAt.Should().BeOnOrBefore(after);
    }

    // ── CancelJobAsync ──────────────────────────────────────────────────

    [Fact]
    public async Task CancelJobAsync_PendingItem_TransitionsToCancelled()
    {
        var request = CreateRequest("owner/repo#4", "provider-4");
        var result = await _distributor.DistributeAsync(request, CancellationToken.None);

        var cancelled = await _distributor.CancelJobAsync(result.WorkItemId!, CancellationToken.None);

        cancelled.Should().BeTrue();
        await using var db = await _dbFactory.CreateDbContextAsync();
        var item = await db.WorkItems.FirstAsync(w => w.Id == Guid.Parse(result.WorkItemId!));
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
        var request = CreateRequest("owner/repo#5", "provider-5");
        var result = await _distributor.DistributeAsync(request, CancellationToken.None);

        var status = await _distributor.GetJobStatusAsync(result.WorkItemId!, CancellationToken.None);

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
        var request = CreateRequest("owner/repo#6", "provider-6");
        var result = await _distributor.DistributeAsync(request, CancellationToken.None);
        await _distributor.CancelJobAsync(result.WorkItemId!, CancellationToken.None);

        var status = await _distributor.GetJobStatusAsync(result.WorkItemId!, CancellationToken.None);

        status.Should().Be(JobDistributionStatus.Cancelled);
    }

    // ── IsIssueDistributedAsync ─────────────────────────────────────────

    [Fact]
    public async Task IsIssueDistributedAsync_PendingItem_ReturnsTrue()
    {
        var request = CreateRequest("owner/repo#7", "provider-7");
        await _distributor.DistributeAsync(request, CancellationToken.None);

        var distributed = await _distributor.IsIssueDistributedAsync(
            "owner/repo#7", "provider-7", CancellationToken.None);

        distributed.Should().BeTrue();
    }

    [Fact]
    public async Task IsIssueDistributedAsync_CancelledItem_ReturnsFalse()
    {
        var request = CreateRequest("owner/repo#8", "provider-8");
        var result = await _distributor.DistributeAsync(request, CancellationToken.None);
        await _distributor.CancelJobAsync(result.WorkItemId!, CancellationToken.None);

        var distributed = await _distributor.IsIssueDistributedAsync(
            "owner/repo#8", "provider-8", CancellationToken.None);

        distributed.Should().BeFalse();
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
        // Manually insert a Dispatched item
        await using var db = await _dbFactory.CreateDbContextAsync();
        db.WorkItems.Add(new WorkItemEntity
        {
            Id = Guid.NewGuid(),
            IssueIdentifier = "owner/repo#9",
            IssueProviderConfigId = "provider-9",
            Status = WorkItemStatus.Dispatched,
            CreatedAt = DateTimeOffset.UtcNow,
            AgentSelector = "kiro",
            TimeoutSeconds = 1800
        });
        await db.SaveChangesAsync();

        var distributed = await _distributor.IsIssueDistributedAsync(
            "owner/repo#9", "provider-9", CancellationToken.None);

        distributed.Should().BeTrue();
    }

    [Fact]
    public async Task IsIssueDistributedAsync_RunningItem_ReturnsTrue()
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        db.WorkItems.Add(new WorkItemEntity
        {
            Id = Guid.NewGuid(),
            IssueIdentifier = "owner/repo#10",
            IssueProviderConfigId = "provider-10",
            Status = WorkItemStatus.Running,
            CreatedAt = DateTimeOffset.UtcNow,
            AgentSelector = "kiro",
            TimeoutSeconds = 1800
        });
        await db.SaveChangesAsync();

        var distributed = await _distributor.IsIssueDistributedAsync(
            "owner/repo#10", "provider-10", CancellationToken.None);

        distributed.Should().BeTrue();
    }

    [Fact]
    public async Task IsIssueDistributedAsync_SucceededItem_ReturnsFalse()
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        db.WorkItems.Add(new WorkItemEntity
        {
            Id = Guid.NewGuid(),
            IssueIdentifier = "owner/repo#11",
            IssueProviderConfigId = "provider-11",
            Status = WorkItemStatus.Succeeded,
            CreatedAt = DateTimeOffset.UtcNow,
            AgentSelector = "kiro",
            TimeoutSeconds = 1800
        });
        await db.SaveChangesAsync();

        var distributed = await _distributor.IsIssueDistributedAsync(
            "owner/repo#11", "provider-11", CancellationToken.None);

        distributed.Should().BeFalse();
    }

    [Fact]
    public async Task IsIssueDistributedAsync_FailedItem_ReturnsFalse()
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        db.WorkItems.Add(new WorkItemEntity
        {
            Id = Guid.NewGuid(),
            IssueIdentifier = "owner/repo#12",
            IssueProviderConfigId = "provider-12",
            Status = WorkItemStatus.Failed,
            CreatedAt = DateTimeOffset.UtcNow,
            AgentSelector = "kiro",
            TimeoutSeconds = 1800
        });
        await db.SaveChangesAsync();

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
