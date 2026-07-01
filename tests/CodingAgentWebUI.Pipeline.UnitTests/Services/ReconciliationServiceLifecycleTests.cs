using AwesomeAssertions;
using CodingAgentWebUI.Infrastructure.Persistence;
using CodingAgentWebUI.Infrastructure.Persistence.Entities;
using CodingAgentWebUI.Infrastructure.Persistence.Services;
using CodingAgentWebUI.Orchestration.Dispatch;
using CodingAgentWebUI.Orchestration.LeaderElection;
using CodingAgentWebUI.Pipeline.Models;
using k8s;
using k8s.Autorest;
using k8s.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using System.Net;
using Xunit;

namespace CodingAgentWebUI.Pipeline.UnitTests.Services;

/// <summary>
/// Lifecycle tests for ReconciliationService: orphan detection, timeout enforcement, stale cleanup.
/// Validates: Requirements 7.1-7.5
/// </summary>
[Trait("Feature", "035a-kubernetes-reconciliation")]
public class ReconciliationServiceLifecycleTests : IDisposable
{
    private readonly DbContextOptions<PipelineDbContext> _dbOptions;
    private readonly TestDbContextFactory _dbFactory;
    private readonly WorkItemTransitionService _transitionService;
    private readonly Mock<IKubernetes> _mockKube;
    private readonly Mock<IBatchV1Operations> _mockBatchV1;

    public ReconciliationServiceLifecycleTests()
    {
        var dbName = $"ReconciliationServiceLifecycle-{Guid.NewGuid()}";
        _dbOptions = new DbContextOptionsBuilder<PipelineDbContext>()
            .UseInMemoryDatabase(dbName)
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;

        using (var ctx = new TestPipelineDbContext(_dbOptions))
            ctx.Database.EnsureCreated();

        _dbFactory = new TestDbContextFactory(_dbOptions);
        _transitionService = new WorkItemTransitionService(_dbFactory, NullLogger<WorkItemTransitionService>.Instance);

        _mockKube = new Mock<IKubernetes> { DefaultValue = DefaultValue.Mock };
        _mockBatchV1 = new Mock<IBatchV1Operations> { DefaultValue = DefaultValue.Mock };
        _mockKube.Setup(k => k.BatchV1).Returns(_mockBatchV1.Object);
    }

    public void Dispose()
    {
        using var db = new TestPipelineDbContext(_dbOptions);
        db.Database.EnsureDeleted();
    }

    // ── Timeout Enforcement ─────────────────────────────────────────────

    [Fact]
    public async Task EnforceTimeouts_TimedOutItem_TransitionsToFailed()
    {
        // Arrange: item created 2 hours ago with 1 hour timeout
        var workItemId = Guid.NewGuid();
        await InsertWorkItem(workItemId, "owner/repo#1", WorkItemStatus.Dispatched,
            createdAt: DateTimeOffset.UtcNow.AddHours(-2), timeoutSeconds: 3600,
            k8sJobName: "caa-timeout1");

        var service = CreateService();

        // Act
        await service.EnforceTimeoutsAsync(CancellationToken.None);

        // Assert
        await using var db = await _dbFactory.CreateDbContextAsync();
        var item = await db.WorkItems.FindAsync(workItemId);
        item!.Status.Should().Be(WorkItemStatus.Failed);
        item.FailureReason.Should().Be(FailureReason.Timeout);
        item.ErrorMessage.Should().Contain("Timeout exceeded");
    }

    [Fact]
    public async Task EnforceTimeouts_NotTimedOut_LeavesUntouched()
    {
        // Arrange: item created 5 minutes ago with 1 hour timeout
        var workItemId = Guid.NewGuid();
        await InsertWorkItem(workItemId, "owner/repo#2", WorkItemStatus.Running,
            createdAt: DateTimeOffset.UtcNow.AddMinutes(-5), timeoutSeconds: 3600);

        var service = CreateService();

        // Act
        await service.EnforceTimeoutsAsync(CancellationToken.None);

        // Assert
        await using var db = await _dbFactory.CreateDbContextAsync();
        var item = await db.WorkItems.FindAsync(workItemId);
        item!.Status.Should().Be(WorkItemStatus.Running);
    }

    [Fact]
    public async Task EnforceTimeouts_RunningItem_AlsoEnforced()
    {
        var workItemId = Guid.NewGuid();
        await InsertWorkItem(workItemId, "owner/repo#3", WorkItemStatus.Running,
            createdAt: DateTimeOffset.UtcNow.AddHours(-3), timeoutSeconds: 1800,
            k8sJobName: "caa-running1");

        var service = CreateService();
        await service.EnforceTimeoutsAsync(CancellationToken.None);

        await using var db = await _dbFactory.CreateDbContextAsync();
        var item = await db.WorkItems.FindAsync(workItemId);
        item!.Status.Should().Be(WorkItemStatus.Failed);
        item.FailureReason.Should().Be(FailureReason.Timeout);
    }

    // ── IsTimedOut static helper ────────────────────────────────────────

    [Fact]
    public void IsTimedOut_ExactlyAtDeadline_ReturnsTrue()
    {
        var created = DateTimeOffset.UtcNow.AddSeconds(-100);
        ReconciliationService.IsTimedOut(created, 100, DateTimeOffset.UtcNow).Should().BeTrue();
    }

    [Fact]
    public void IsTimedOut_BeforeDeadline_ReturnsFalse()
    {
        var created = DateTimeOffset.UtcNow.AddSeconds(-50);
        ReconciliationService.IsTimedOut(created, 100, DateTimeOffset.UtcNow).Should().BeFalse();
    }

    // ── Stale Cleanup ───────────────────────────────────────────────────

    [Fact(Skip = "ExecuteDeleteAsync not supported by EF Core InMemory provider")]
    public async Task CleanupStaleWorkItems_OldTerminalItems_AreDeleted()
    {
        // Arrange: Succeeded item completed 10 days ago (retention = 7 days)
        var staleId = Guid.NewGuid();
        await InsertWorkItem(staleId, "owner/repo#stale", WorkItemStatus.Succeeded,
            createdAt: DateTimeOffset.UtcNow.AddDays(-10),
            completedAt: DateTimeOffset.UtcNow.AddDays(-10));

        // Fresh Succeeded item completed 1 day ago (within retention)
        var freshId = Guid.NewGuid();
        await InsertWorkItem(freshId, "owner/repo#fresh", WorkItemStatus.Succeeded,
            createdAt: DateTimeOffset.UtcNow.AddDays(-1),
            completedAt: DateTimeOffset.UtcNow.AddDays(-1));

        var service = CreateService(retentionDays: 7);
        await service.CleanupStaleWorkItemsAsync(CancellationToken.None);

        await using var db = await _dbFactory.CreateDbContextAsync();
        var staleItem = await db.WorkItems.FindAsync(staleId);
        var freshItem = await db.WorkItems.FindAsync(freshId);

        staleItem.Should().BeNull("stale item should be deleted");
        freshItem.Should().NotBeNull("fresh item should be retained");
    }

    [Fact(Skip = "ExecuteDeleteAsync not supported by EF Core InMemory provider")]
    public async Task CleanupStaleWorkItems_ActiveItems_NeverDeleted()
    {
        var activeId = Guid.NewGuid();
        await InsertWorkItem(activeId, "owner/repo#active", WorkItemStatus.Running,
            createdAt: DateTimeOffset.UtcNow.AddDays(-30));

        var service = CreateService(retentionDays: 7);
        await service.CleanupStaleWorkItemsAsync(CancellationToken.None);

        await using var db = await _dbFactory.CreateDbContextAsync();
        var item = await db.WorkItems.FindAsync(activeId);
        item.Should().NotBeNull("active items must never be deleted regardless of age");
    }

    // ── IsStale static helper ───────────────────────────────────────────

    [Fact]
    public void IsStale_NullCompletedAt_ReturnsFalse()
    {
        ReconciliationService.IsStale(null, 7, DateTimeOffset.UtcNow).Should().BeFalse();
    }

    [Fact]
    public void IsStale_CompletedBeyondRetention_ReturnsTrue()
    {
        var completedAt = DateTimeOffset.UtcNow.AddDays(-10);
        ReconciliationService.IsStale(completedAt, 7, DateTimeOffset.UtcNow).Should().BeTrue();
    }

    [Fact]
    public void IsStale_CompletedWithinRetention_ReturnsFalse()
    {
        var completedAt = DateTimeOffset.UtcNow.AddDays(-3);
        ReconciliationService.IsStale(completedAt, 7, DateTimeOffset.UtcNow).Should().BeFalse();
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    private ReconciliationService CreateService(int retentionDays = 7)
    {
        var configData = new Dictionary<string, string?>
        {
            ["WorkDistribution:Reconciliation:PollIntervalSeconds"] = "30",
            ["WorkDistribution:Reconciliation:RetentionDays"] = retentionDays.ToString(),
            ["WorkDistribution:Namespace"] = "default"
        };

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(configData)
            .Build();

        var leaderElection = new LeaderElectionService(Options.Create(new LeaderElectionOptions()));
        // Force leader
        var field = typeof(LeaderElectionService).GetField("_isLeader",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        field?.SetValue(leaderElection, true);

        return new ReconciliationService(
            _dbFactory, leaderElection, _mockKube.Object,
            _transitionService, config, null);
    }

    private async Task InsertWorkItem(Guid id, string issueId, WorkItemStatus status,
        DateTimeOffset? createdAt = null, int timeoutSeconds = 1800,
        string? k8sJobName = null, DateTimeOffset? completedAt = null)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        db.WorkItems.Add(new WorkItemEntity
        {
            Id = id,
            IssueIdentifier = issueId,
            IssueProviderConfigId = "provider-1",
            Status = status,
            AgentSelector = "kiro,dotnet",
            CreatedAt = createdAt ?? DateTimeOffset.UtcNow,
            TimeoutSeconds = timeoutSeconds,
            K8sJobName = k8sJobName,
            CompletedAt = completedAt,
            Payload = "{}"
        });
        await db.SaveChangesAsync();
    }

    // ── Test Infrastructure ─────────────────────────────────────────────

    private sealed class TestPipelineDbContext : PipelineDbContext
    {
        public TestPipelineDbContext(DbContextOptions<PipelineDbContext> options) : base(options) { }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);
            foreach (var entityType in modelBuilder.Model.GetEntityTypes())
            {
                var rowVersion = entityType.FindProperty("RowVersion");
                if (rowVersion != null)
                {
                    rowVersion.IsConcurrencyToken = false;
                    rowVersion.ValueGenerated = Microsoft.EntityFrameworkCore.Metadata.ValueGenerated.Never;
                }
            }
            foreach (var entityType in modelBuilder.Model.GetEntityTypes())
            {
                foreach (var index in entityType.GetIndexes().Where(i => i.GetFilter() != null).ToList())
                    entityType.RemoveIndex(index);
            }
        }
    }

    private sealed class TestDbContextFactory : IDbContextFactory<PipelineDbContext>
    {
        private readonly DbContextOptions<PipelineDbContext> _options;
        public TestDbContextFactory(DbContextOptions<PipelineDbContext> options) => _options = options;
        public PipelineDbContext CreateDbContext() => new TestPipelineDbContext(_options);
        public Task<PipelineDbContext> CreateDbContextAsync(CancellationToken ct = default)
            => Task.FromResult(CreateDbContext());
    }
}
