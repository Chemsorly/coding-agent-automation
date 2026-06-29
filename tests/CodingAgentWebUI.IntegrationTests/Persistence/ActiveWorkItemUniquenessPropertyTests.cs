// Feature: Persistence Integration Tests
// Property 2: Active WorkItem Uniqueness — Application-level dedup prevents duplicate active work items
using AwesomeAssertions;
using CodingAgentWebUI.Infrastructure.Persistence;
using CodingAgentWebUI.Infrastructure.Persistence.Entities;
using CodingAgentWebUI.Infrastructure.Persistence.Services;
using CodingAgentWebUI.Orchestration.Dispatch;
using CodingAgentWebUI.Pipeline.Models;
using FsCheck;
using FsCheck.Fluent;
using FsCheck.Xunit;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodingAgentWebUI.IntegrationTests.Persistence;

/// <summary>
/// Property 2: Active WorkItem Uniqueness.
/// Since InMemory provider doesn't support partial unique indexes, this tests
/// the application-level deduplication: once a work item is active (Pending/Dispatched/Running),
/// IsIssueDistributedAsync returns true, preventing the pipeline from dispatching duplicates.
/// Uses InMemory EF Core provider.
/// </summary>
public class ActiveWorkItemUniquenessPropertyTests : IDisposable
{
    private static readonly WorkItemStatus[] ActiveStatuses =
    [
        WorkItemStatus.Pending,
        WorkItemStatus.Dispatched,
        WorkItemStatus.Running,
    ];

    private readonly DbContextOptions<PipelineDbContext> _dbOptions;
    private readonly InMemoryDbContextFactory _dbFactory;
    private readonly KubernetesWorkDistributor _distributor;

    public ActiveWorkItemUniquenessPropertyTests()
    {
        var dbName = $"WorkItemUniqueness-{Guid.NewGuid()}";
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
    /// Property 2: Active WorkItem Uniqueness (application-level dedup).
    /// For any active status, inserting a work item with that status causes
    /// IsIssueDistributedAsync to return true for the same issue+provider pair,
    /// which the pipeline uses to prevent duplicate dispatch.
    /// </summary>
    [Property(Arbitrary = new[] { typeof(ActiveWorkItemArbitraries) })]
    public async Task<bool> ActiveWorkItem_IsIssueDistributed_ReturnsTrue(WorkItemStatus activeStatus)
    {
        // Only test active statuses
        if (!ActiveStatuses.Contains(activeStatus))
            return true; // vacuously true for non-active statuses (filtered by generator)

        var issueId = $"owner/repo#{Guid.NewGuid():N}";
        var providerId = $"provider-{Guid.NewGuid():N}";

        // Insert a work item with the given active status
        await using (var db = await _dbFactory.CreateDbContextAsync())
        {
            db.WorkItems.Add(new WorkItemEntity
            {
                Id = Guid.NewGuid(),
                IssueIdentifier = issueId,
                IssueProviderConfigId = providerId,
                Status = activeStatus,
                CreatedAt = DateTimeOffset.UtcNow,
                AgentSelector = "test-agent",
                TimeoutSeconds = 1800,
            });
            await db.SaveChangesAsync();
        }

        // Application-level dedup check: IsIssueDistributedAsync should return true
        var isDistributed = await _distributor.IsIssueDistributedAsync(
            issueId, providerId, CancellationToken.None);

        return isDistributed;
    }

    /// <summary>
    /// Property 2 (converse): After distributing a work item via DistributeAsync,
    /// attempting to check the same issue shows it as already distributed,
    /// preventing the pipeline from creating a duplicate.
    /// </summary>
    [Property(Arbitrary = new[] { typeof(ActiveWorkItemArbitraries) })]
    public async Task<bool> DistributeThenCheck_PreventsSecondDispatch(NonEmptyString issueIdSuffix)
    {
        var issueId = $"owner/repo#{issueIdSuffix.Get.Replace(" ", "")}";
        var providerId = $"provider-{Guid.NewGuid():N}";

        // First distribute
        var request = new JobDistributionRequest
        {
            IssueIdentifier = issueId,
            IssueProviderConfigId = providerId,
            RepoProviderConfigId = "repo-1",
            InitiatedBy = "pipeline-loop",
            TaskType = WorkItemTaskType.Implementation,
            AgentSelector = "test-agent",
            TimeoutSeconds = 1800,
            ProjectId = "proj-1",
            RunType = PipelineRunType.Implementation
        };

        var result = await _distributor.DistributeAsync(request, CancellationToken.None);
        if (!result.Success) return false;

        // Check dedup: same issue should show as distributed
        var isDistributed = await _distributor.IsIssueDistributedAsync(
            issueId, providerId, CancellationToken.None);

        return isDistributed;
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

/// <summary>
/// FsCheck arbitrary generators for active work item uniqueness (Property 2).
/// Generates only active statuses (Pending, Dispatched, Running).
/// </summary>
public class ActiveWorkItemArbitraries
{
    public static Arbitrary<WorkItemStatus> WorkItemStatusArb()
    {
        var gen = Gen.Elements(
            WorkItemStatus.Pending,
            WorkItemStatus.Dispatched,
            WorkItemStatus.Running);
        return gen.ToArbitrary();
    }
}
