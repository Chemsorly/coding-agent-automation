// Feature: 035a-postgres-work-queue
// Property 3: IsIssueDistributed Consistency
using System.Text.Json;
using FsCheck;
using FsCheck.Fluent;
using FsCheck.Xunit;
using CodingAgentWebUI.Infrastructure.Persistence;
using CodingAgentWebUI.Infrastructure.Persistence.Entities;
using CodingAgentWebUI.Infrastructure.Persistence.Services;
using CodingAgentWebUI.Orchestration.Dispatch;
using CodingAgentWebUI.Pipeline.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodingAgentWebUI.Infrastructure.UnitTests.Persistence;

/// <summary>
/// Property-based test asserting that IsIssueDistributedAsync returns true
/// if and only if the work item's status is in {Pending, Dispatched, Running}.
/// Uses KubernetesWorkDistributor (queries WorkItems table directly).
/// **Validates: Requirements 4.6**
/// </summary>
public class IsIssueDistributedConsistencyPropertyTests : IDisposable
{
    private static readonly HashSet<WorkItemStatus> ActiveStatuses =
    [
        WorkItemStatus.Pending,
        WorkItemStatus.Dispatched,
        WorkItemStatus.Running,
    ];

    private readonly DbContextOptions<PipelineDbContext> _dbOptions;
    private readonly InMemoryDbContextFactory _dbFactory;
    private readonly KubernetesWorkDistributor _distributor;

    public IsIssueDistributedConsistencyPropertyTests()
    {
        var dbName = $"IsIssueDistributedPropTests-{Guid.NewGuid()}";
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

    /// <summary>
    /// Property 3: IsIssueDistributed Consistency
    /// For any generated WorkItemStatus, inserting a work item with that status
    /// and calling IsIssueDistributedAsync returns true iff status is in {Pending, Dispatched, Running}.
    /// **Validates: Requirements 4.6**
    /// </summary>
    [Property(MaxTest = 20, Arbitrary = new[] { typeof(IsIssueDistributedArbitraries) })]
    public async Task<bool> IsIssueDistributed_ReturnsTrue_IffStatusIsActive(WorkItemStatus status)
    {
        // Generate unique identifiers per test iteration to avoid cross-pollution
        var issueId = $"owner/repo#{Guid.NewGuid():N}";
        var providerId = $"provider-{Guid.NewGuid():N}";

        // Insert work item with given status
        await using (var db = await _dbFactory.CreateDbContextAsync())
        {
            db.WorkItems.Add(new WorkItemEntity
            {
                Id = Guid.NewGuid(),
                IssueIdentifier = issueId,
                IssueProviderConfigId = providerId,
                Status = status,
                CreatedAt = DateTimeOffset.UtcNow,
                AgentSelector = "test",
                TimeoutSeconds = 1800,
            });
            await db.SaveChangesAsync();
        }

        // Query
        var result = await _distributor.IsIssueDistributedAsync(issueId, providerId, CancellationToken.None);

        // Assert: true iff status is active (Pending, Dispatched, Running)
        var expected = ActiveStatuses.Contains(status);
        return result == expected;
    }

    // ── Test Infrastructure ─────────────────────────────────────────────

    private sealed class InMemoryPipelineDbContext : PipelineDbContext
    {
        public InMemoryPipelineDbContext(DbContextOptions<PipelineDbContext> options)
            : base(options) { }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);

            var jsonConverter = new ValueConverter<JsonDocument?, string?>(
                v => v == null ? null : v.RootElement.GetRawText(),
                v => v == null ? null : JsonDocument.Parse(v, default));

            foreach (var entityType in modelBuilder.Model.GetEntityTypes())
            {
                foreach (var property in entityType.GetProperties())
                {
                    if (property.ClrType == typeof(JsonDocument))
                    {
                        property.SetValueConverter(jsonConverter);
                        property.SetColumnType(null);
                    }
                }

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

/// <summary>
/// FsCheck arbitrary generator for WorkItemStatus (Property 3).
/// Generates all 6 possible status values uniformly.
/// </summary>
public class IsIssueDistributedArbitraries
{
    public static Arbitrary<WorkItemStatus> WorkItemStatusArb()
    {
        var gen = Gen.Elements(
            WorkItemStatus.Pending,
            WorkItemStatus.Dispatched,
            WorkItemStatus.Running,
            WorkItemStatus.Succeeded,
            WorkItemStatus.Failed,
            WorkItemStatus.Cancelled);
        return gen.ToArbitrary();
    }
}
