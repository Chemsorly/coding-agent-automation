using AwesomeAssertions;
using CodingAgentWebUI.Infrastructure.Persistence;
using CodingAgentWebUI.Infrastructure.Persistence.Entities;
using CodingAgentWebUI.Infrastructure.Persistence.Services;
using CodingAgentWebUI.Pipeline.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodingAgentWebUI.Infrastructure.UnitTests.Persistence;

/// <summary>
/// Tests for WorkItemTransitionService.TransitionIfAsync — the compare-and-swap method.
/// Key invariant: TransitionIfAsync is NOT idempotent. It returns false when the
/// current status does not exactly match expectedCurrent, including when it already equals target.
/// Uses EF InMemory — adequate for unit-testing the CAS guard logic. Real Postgres
/// concurrency (xmin enforcement) is tested separately per Req 4.5c.
/// </summary>
public class TransitionIfAsyncTests
{
    // ── Test 1: Matching expected → succeeds ───────────────────────────────

    [Fact]
    public async Task TransitionIfAsync_MatchingExpected_Succeeds()
    {
        // Arrange: row is Dispatched
        var id = Guid.NewGuid();
        var factory = await CreateFactoryWithItem(id, WorkItemStatus.Dispatched);
        var svc = new WorkItemTransitionService(factory, NullLogger<WorkItemTransitionService>.Instance);

        // Act: expectedCurrent=Dispatched, target=Running
        var result = await svc.TransitionIfAsync(id, WorkItemStatus.Dispatched, WorkItemStatus.Running);

        // Assert: returns true, row is now Running
        result.Should().BeTrue();
        await AssertStatus(factory, id, WorkItemStatus.Running);
    }

    // ── Test 2: Wrong expected → returns false ────────────────────────────

    [Fact]
    public async Task TransitionIfAsync_WrongExpected_ReturnsFalse()
    {
        // Arrange: row is Running
        var id = Guid.NewGuid();
        var factory = await CreateFactoryWithItem(id, WorkItemStatus.Running);
        var svc = new WorkItemTransitionService(factory, NullLogger<WorkItemTransitionService>.Instance);

        // Act: expectedCurrent=Dispatched (wrong), target=Running
        var result = await svc.TransitionIfAsync(id, WorkItemStatus.Dispatched, WorkItemStatus.Running);

        // Assert: returns false, row unchanged
        result.Should().BeFalse();
        await AssertStatus(factory, id, WorkItemStatus.Running);
    }

    // ── Test 3: Already at target → returns false (NOT idempotent) ────────

    [Fact]
    public async Task TransitionIfAsync_AlreadyAtTarget_ReturnsFalse()
    {
        // Arrange: row is Running — same as target
        var id = Guid.NewGuid();
        var factory = await CreateFactoryWithItem(id, WorkItemStatus.Running);
        var svc = new WorkItemTransitionService(factory, NullLogger<WorkItemTransitionService>.Instance);

        // Act: expectedCurrent=Running, target=Running (already there)
        var result = await svc.TransitionIfAsync(id, WorkItemStatus.Running, WorkItemStatus.Running);

        // Assert: returns false — NOT idempotent like TransitionAsync
        result.Should().BeFalse();
    }

    // ── Test 4: Concurrent claims — exactly one succeeds ──────────────────

    [Fact]
    public async Task TransitionIfAsync_ConcurrentClaims_OnlyOneSucceeds()
    {
        // Arrange: single Dispatched row, two separate service instances (each with its own factory)
        var id = Guid.NewGuid();
        var dbOptions = CreateInMemoryDbOptions();
        await SeedItem(dbOptions, id, WorkItemStatus.Dispatched);

        var factory1 = new DirectDbContextFactory(dbOptions);
        var factory2 = new DirectDbContextFactory(dbOptions);
        var svc1 = new WorkItemTransitionService(factory1, NullLogger<WorkItemTransitionService>.Instance);
        var svc2 = new WorkItemTransitionService(factory2, NullLogger<WorkItemTransitionService>.Instance);

        // Act: both callers attempt to move Dispatched→Running concurrently
        var t1 = svc1.TransitionIfAsync(id, WorkItemStatus.Dispatched, WorkItemStatus.Running);
        var t2 = svc2.TransitionIfAsync(id, WorkItemStatus.Dispatched, WorkItemStatus.Running);
        var results = await Task.WhenAll(t1, t2);

        // Assert: exactly one true, one false
        results.Should().ContainSingle(r => r == true, "exactly one claim must succeed");
        results.Should().ContainSingle(r => r == false, "exactly one claim must fail");
    }

    // ── Test 5: Mutate callback applied on success ────────────────────────

    [Fact]
    public async Task TransitionIfAsync_MutateCallback_AppliedOnSuccess()
    {
        // Arrange: row is Dispatched
        var id = Guid.NewGuid();
        var factory = await CreateFactoryWithItem(id, WorkItemStatus.Dispatched);
        var svc = new WorkItemTransitionService(factory, NullLogger<WorkItemTransitionService>.Instance);

        const string agentId = "test-agent-001";

        // Act: transition with mutate callback
        var result = await svc.TransitionIfAsync(
            id,
            WorkItemStatus.Dispatched,
            WorkItemStatus.Running,
            mutate: entity =>
            {
                entity.AssignedAgentId = agentId;
            });

        // Assert: success and fields persisted
        result.Should().BeTrue();
        await using var db = new InMemoryPipelineDbContext(factory.Options);
        var item = await db.WorkItems.FindAsync(id);
        item!.Status.Should().Be(WorkItemStatus.Running);
        item.AssignedAgentId.Should().Be(agentId);
    }

    // ── Test 6: Invalid transition → returns false ────────────────────────

    [Fact]
    public async Task TransitionIfAsync_InvalidTransition_ReturnsFalse()
    {
        // Arrange: row is Pending — Pending→Succeeded is not a valid transition
        var id = Guid.NewGuid();
        var factory = await CreateFactoryWithItem(id, WorkItemStatus.Pending);
        var svc = new WorkItemTransitionService(factory, NullLogger<WorkItemTransitionService>.Instance);

        // Act: expectedCurrent=Pending, target=Succeeded (invalid per IsValidTransition)
        var result = await svc.TransitionIfAsync(id, WorkItemStatus.Pending, WorkItemStatus.Succeeded);

        // Assert: returns false, row unchanged
        result.Should().BeFalse();
        await AssertStatus(factory, id, WorkItemStatus.Pending);
    }

    // ── Test infrastructure ────────────────────────────────────────────────

    private static DbContextOptions<PipelineDbContext> CreateInMemoryDbOptions()
        => new DbContextOptionsBuilder<PipelineDbContext>()
            .UseInMemoryDatabase(databaseName: $"TransitionIfTests-{Guid.NewGuid()}")
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;

    private static async Task SeedItem(
        DbContextOptions<PipelineDbContext> options,
        Guid id,
        WorkItemStatus status)
    {
        await using var db = new InMemoryPipelineDbContext(options);
        db.Database.EnsureCreated();
        db.WorkItems.Add(new WorkItemEntity
        {
            Id = id,
            IssueIdentifier = "owner/repo#1",
            IssueProviderConfigId = "ip-1",
            Status = status,
            CreatedAt = DateTimeOffset.UtcNow,
            TaskType = WorkItemTaskType.Implementation
        });
        await db.SaveChangesAsync();
    }

    private static async Task<DirectDbContextFactory> CreateFactoryWithItem(Guid id, WorkItemStatus status)
    {
        var opts = CreateInMemoryDbOptions();
        await SeedItem(opts, id, status);
        return new DirectDbContextFactory(opts);
    }

    private static async Task AssertStatus(
        DirectDbContextFactory factory,
        Guid id,
        WorkItemStatus expected)
    {
        await using var db = new InMemoryPipelineDbContext(factory.Options);
        var item = await db.WorkItems.FindAsync(id);
        item!.Status.Should().Be(expected);
    }

    // ── Shared InMemory DbContext (strips row-version / filtered indexes) ──

    private class InMemoryPipelineDbContext : PipelineDbContext
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
                    entityType.RemoveIndex(index);
            }
        }
    }

    // ── Simple factory wrapping a fixed options instance ──────────────────

    private sealed class DirectDbContextFactory : IDbContextFactory<PipelineDbContext>
    {
        public DbContextOptions<PipelineDbContext> Options { get; }

        public DirectDbContextFactory(DbContextOptions<PipelineDbContext> options)
            => Options = options;

        public PipelineDbContext CreateDbContext()
            => new InMemoryPipelineDbContext(Options);

        public Task<PipelineDbContext> CreateDbContextAsync(CancellationToken ct = default)
            => Task.FromResult(CreateDbContext());
    }
}
