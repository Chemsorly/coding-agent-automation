using AwesomeAssertions;
using CodingAgentWebUI.Infrastructure.Persistence;
using CodingAgentWebUI.Infrastructure.Persistence.Entities;
using CodingAgentWebUI.Infrastructure.Persistence.Services;
using CodingAgentWebUI.Orchestration.Dispatch;
using CodingAgentWebUI.Pipeline.LeaderElection;
using CodingAgentWebUI.Pipeline.Models;
using k8s;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using System.Reflection;
using Xunit;

namespace CodingAgentWebUI.IntegrationTests.Persistence;

/// <summary>
/// Integration tests for ReconciliationService stale WorkItem cleanup.
/// Uses SQLite (named shared-cache in-memory) because EF Core's ExecuteDeleteAsync
/// is not supported by the InMemory provider — see issue #1974.
/// SQLite fully supports server-side bulk delete, making these tests executable.
/// </summary>
[Trait("Feature", "035a-kubernetes-reconciliation")]
public class ReconciliationServiceCleanupIntegrationTests : IDisposable
{
    // A single SqliteConnection held open for the lifetime of the test instance.
    // Named shared-cache in-memory (Data Source=<name>;Mode=Memory;Cache=Shared) means
    // the database lives as long as at least one connection with this name is open.
    // Without holding this anchor connection open, IDbContextFactory would destroy the DB
    // between CreateDbContextAsync() calls, causing assertions to pass vacuously on an empty DB.
    private readonly SqliteConnection _sqliteConnection;

    private readonly DbContextOptions<PipelineDbContext> _dbOptions;
    private readonly TestDbContextFactory _dbFactory;
    private readonly WorkItemTransitionService _transitionService;
    private readonly Mock<IKubernetes> _mockKube;
    private readonly Mock<IBatchV1Operations> _mockBatchV1;

    public ReconciliationServiceCleanupIntegrationTests()
    {
        var dbName = $"ReconciliationServiceCleanup-{Guid.NewGuid()}";
        var connectionString = $"Data Source={dbName};Mode=Memory;Cache=Shared";
        _sqliteConnection = new SqliteConnection(connectionString);
        _sqliteConnection.Open();

        _dbOptions = new DbContextOptionsBuilder<PipelineDbContext>()
            .UseSqlite(_sqliteConnection)
            .Options;

        using (var ctx = new TestSqlitePipelineDbContext(_dbOptions))
            ctx.Database.EnsureCreated();

        _dbFactory = new TestDbContextFactory(_dbOptions);
        _transitionService = new WorkItemTransitionService(_dbFactory, NullLogger<WorkItemTransitionService>.Instance);

        _mockKube = new Mock<IKubernetes> { DefaultValue = DefaultValue.Mock };
        _mockBatchV1 = new Mock<IBatchV1Operations> { DefaultValue = DefaultValue.Mock };
        _mockKube.Setup(k => k.BatchV1).Returns(_mockBatchV1.Object);
    }

    public void Dispose()
    {
        _sqliteConnection.Close();
        _sqliteConnection.Dispose();
    }

    // ── Stale Cleanup ──────────────────────────────────────────────────

    [Fact]
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

    [Fact]
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

        // Set _isLeader = true so the service behaves as the current leader
        var isLeaderField = typeof(LeaderElectionService).GetField("_isLeader",
            BindingFlags.NonPublic | BindingFlags.Instance);
        isLeaderField?.SetValue(leaderElection, true);

        // Initialize _leaderCts so LeaderToken returns a non-cancelled token
        var leaderCtsField = typeof(LeaderElectionService).GetField("_leaderCts",
            BindingFlags.NonPublic | BindingFlags.Instance);
        leaderCtsField?.SetValue(leaderElection, new CancellationTokenSource());

        return new ReconciliationService(
            new ReconciliationServiceDependencies(_dbFactory, leaderElection, _mockKube.Object,
                _transitionService, config));
    }

    private async Task InsertWorkItem(Guid id, string issueId, WorkItemStatus status,
        DateTimeOffset? createdAt = null, int timeoutSeconds = 1800,
        string? k8sJobName = null, DateTimeOffset? completedAt = null,
        DateTimeOffset? dispatchedAt = null, DateTimeOffset? lastProgressAt = null,
        string? claimedPvcName = null)
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
            DispatchedAt = dispatchedAt ?? createdAt,
            TimeoutSeconds = timeoutSeconds,
            K8sJobName = k8sJobName,
            CompletedAt = completedAt,
            LastProgressAt = lastProgressAt,
            ClaimedPvcName = claimedPvcName,
            Payload = "{}"
        });
        await db.SaveChangesAsync();
    }

    // ── Test Infrastructure ─────────────────────────────────────────────

    /// <summary>
    /// SQLite-compatible PipelineDbContext subclass.
    /// Strips PostgreSQL-specific model annotations that are incompatible with SQLite:
    /// - RowVersion concurrency tokens (not supported by SQLite provider)
    /// - Filtered indexes (PostgreSQL partial index syntax)
    /// - JSONB column types (unrecognized by SQLite; stripping is defensive and matches codebase convention)
    /// Also adds DateTimeOffset value converters so EF Core SQLite can translate comparisons
    /// (e.g. CompletedAt &lt; cutoff) in ExecuteDeleteAsync bulk-delete predicates.
    /// Without these converters, EF Core 10 SQLite cannot translate DateTimeOffset comparisons
    /// in ExecuteDelete statements — they are stored as UTC ticks (long) for reliable comparison.
    /// </summary>
    private sealed class TestSqlitePipelineDbContext : PipelineDbContext
    {
        public TestSqlitePipelineDbContext(DbContextOptions<PipelineDbContext> options)
            : base(options) { }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);

            foreach (var entityType in modelBuilder.Model.GetEntityTypes())
            {
                // Strip RowVersion concurrency tokens
                var rowVersion = entityType.FindProperty("RowVersion");
                if (rowVersion != null)
                {
                    rowVersion.IsConcurrencyToken = false;
                    rowVersion.ValueGenerated = Microsoft.EntityFrameworkCore.Metadata.ValueGenerated.Never;
                }

                // Strip filtered indexes (PostgreSQL partial index syntax)
                foreach (var index in entityType.GetIndexes().Where(i => i.GetFilter() != null).ToList())
                    entityType.RemoveIndex(index);

                // Strip JSONB column type annotations
                foreach (var property in entityType.GetProperties())
                {
                    if (property.GetColumnType() == "jsonb")
                        property.SetColumnType(null);
                }
            }

            // Add DateTimeOffset → long (UTC ticks) value converters so that EF Core SQLite
            // can translate DateTimeOffset comparisons in ExecuteDeleteAsync bulk-delete predicates.
            // Without this, EF Core 10 SQLite throws InvalidOperationException when the predicate
            // contains DateTimeOffset comparisons (e.g. CompletedAt < cutoff).
            var dateTimeOffsetConverter = new Microsoft.EntityFrameworkCore.Storage.ValueConversion.ValueConverter<DateTimeOffset, long>(
                v => v.UtcTicks,
                v => new DateTimeOffset(v, TimeSpan.Zero));
            var nullableDateTimeOffsetConverter = new Microsoft.EntityFrameworkCore.Storage.ValueConversion.ValueConverter<DateTimeOffset?, long?>(
                v => v == null ? null : (long?)v.Value.UtcTicks,
                v => v == null ? null : (DateTimeOffset?)new DateTimeOffset(v.Value, TimeSpan.Zero));

            foreach (var entityType in modelBuilder.Model.GetEntityTypes())
            {
                foreach (var property in entityType.GetProperties())
                {
                    if (property.ClrType == typeof(DateTimeOffset))
                        property.SetValueConverter(dateTimeOffsetConverter);
                    else if (property.ClrType == typeof(DateTimeOffset?))
                        property.SetValueConverter(nullableDateTimeOffsetConverter);
                }
            }
        }
    }

    private sealed class TestDbContextFactory : IDbContextFactory<PipelineDbContext>
    {
        private readonly DbContextOptions<PipelineDbContext> _options;

        public TestDbContextFactory(DbContextOptions<PipelineDbContext> options)
            => _options = options;

        public PipelineDbContext CreateDbContext()
            => new TestSqlitePipelineDbContext(_options);

        public Task<PipelineDbContext> CreateDbContextAsync(CancellationToken ct = default)
            => Task.FromResult(CreateDbContext());
    }
}
