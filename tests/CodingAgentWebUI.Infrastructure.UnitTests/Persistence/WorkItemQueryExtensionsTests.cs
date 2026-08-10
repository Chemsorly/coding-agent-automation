using AwesomeAssertions;
using CodingAgentWebUI.Infrastructure.Persistence;
using CodingAgentWebUI.Infrastructure.Persistence.Entities;
using CodingAgentWebUI.Pipeline.Models;
using FsCheck;
using FsCheck.Fluent;
using FsCheck.Xunit;
using Microsoft.EntityFrameworkCore;

namespace CodingAgentWebUI.Infrastructure.UnitTests.Persistence;

/// <summary>
/// Unit tests for <see cref="WorkItemQueryExtensions.WhereActive"/>.
/// Verifies that the extension correctly filters to Dispatched and Running work items only,
/// covering all six <see cref="WorkItemStatus"/> enum values.
/// </summary>
public sealed class WorkItemQueryExtensionsTests : IDisposable
{
    private readonly DbContextOptions<PipelineDbContext> _options;

    public WorkItemQueryExtensionsTests()
    {
        _options = new DbContextOptionsBuilder<PipelineDbContext>()
            .UseInMemoryDatabase(databaseName: $"WhereActive_{Guid.NewGuid()}")
            .Options;
    }

    // ── Inclusion tests ───────────────────────────────────────────────────

    [Fact]
    public async Task WhereActive_IncludesDispatchedItems()
    {
        await SeedAsync(WorkItemStatus.Dispatched);
        await using var db = new InMemoryPipelineDbContext(_options);

        var results = await db.WorkItems.WhereActive().ToListAsync();

        results.Should().ContainSingle(w => w.Status == WorkItemStatus.Dispatched);
    }

    [Fact]
    public async Task WhereActive_IncludesRunningItems()
    {
        await SeedAsync(WorkItemStatus.Running);
        await using var db = new InMemoryPipelineDbContext(_options);

        var results = await db.WorkItems.WhereActive().ToListAsync();

        results.Should().ContainSingle(w => w.Status == WorkItemStatus.Running);
    }

    // ── Exclusion tests ───────────────────────────────────────────────────

    [Fact]
    public async Task WhereActive_ExcludesPendingItems()
    {
        await SeedAsync(WorkItemStatus.Pending);
        await using var db = new InMemoryPipelineDbContext(_options);

        var results = await db.WorkItems.WhereActive().ToListAsync();

        results.Should().BeEmpty();
    }

    [Fact]
    public async Task WhereActive_ExcludesSucceededItems()
    {
        await SeedAsync(WorkItemStatus.Succeeded);
        await using var db = new InMemoryPipelineDbContext(_options);

        var results = await db.WorkItems.WhereActive().ToListAsync();

        results.Should().BeEmpty();
    }

    [Fact]
    public async Task WhereActive_ExcludesFailedItems()
    {
        await SeedAsync(WorkItemStatus.Failed);
        await using var db = new InMemoryPipelineDbContext(_options);

        var results = await db.WorkItems.WhereActive().ToListAsync();

        results.Should().BeEmpty();
    }

    [Fact]
    public async Task WhereActive_ExcludesCancelledItems()
    {
        await SeedAsync(WorkItemStatus.Cancelled);
        await using var db = new InMemoryPipelineDbContext(_options);

        var results = await db.WorkItems.WhereActive().ToListAsync();

        results.Should().BeEmpty();
    }

    // ── Mixed-set test ────────────────────────────────────────────────────

    [Fact]
    public async Task WhereActive_OnlyReturnsActiveFromMixedSet()
    {
        // Seed one item per status (6 total)
        await using (var db = new InMemoryPipelineDbContext(_options))
        {
            foreach (var status in Enum.GetValues<WorkItemStatus>())
                db.WorkItems.Add(MakeWorkItem(status));
            await db.SaveChangesAsync();
        }

        await using var readDb = new InMemoryPipelineDbContext(_options);
        var results = await readDb.WorkItems.WhereActive().ToListAsync();

        // TODO: The count assertion below will produce a confusing failure message ("Expected 2 but found N")
        // if a new status is added to WorkItemStatus *and* also added to WhereActive(). Consider replacing
        // HaveCount(2) with an exact-set assertion such as:
        //   results.Select(w => w.Status).Should().BeEquivalentTo(new[] { WorkItemStatus.Dispatched, WorkItemStatus.Running });
        // That makes the contract self-describing and the failure message immediately diagnostic.
        results.Should().HaveCount(2);
        results.Should().Contain(w => w.Status == WorkItemStatus.Dispatched);
        results.Should().Contain(w => w.Status == WorkItemStatus.Running);
    }

    // ── EF Core translatability ───────────────────────────────────────────

    [Fact]
    public async Task WhereActive_IsEfCoreTranslatable()
    {
        // TODO: This test does not actually distinguish EF Core provider translation from plain
        // in-memory LINQ evaluation. The InMemory EF Core provider evaluates predicates client-side,
        // so a predicate that is untranslatable by the real Postgres provider (e.g. one calling a
        // C# method with no SQL equivalent) would still pass here. To truly verify SQL translatability,
        // this test would need to run against a real Postgres instance (integration test). As-is the
        // test verifies LINQ composition only, not provider translation.
        // Verify that WhereActive() composes correctly with other LINQ operators
        // on an EF Core IQueryable (not just in-memory LINQ-to-objects).
        await SeedAsync(WorkItemStatus.Dispatched);
        await using var db = new InMemoryPipelineDbContext(_options);

        // Chain additional operators after WhereActive() — mimics real callsite usage
        var count = await db.WorkItems
            .WhereActive()
            .Select(w => w.Id)
            .CountAsync();

        count.Should().Be(1);
    }

    // ── Property-based test ───────────────────────────────────────────────

    /// <summary>
    /// For every <see cref="WorkItemStatus"/> value, <see cref="WorkItemQueryExtensions.WhereActive"/>
    /// returns the item if and only if the status is Dispatched or Running.
    /// </summary>
    [Property(MaxTest = 20, Arbitrary = new[] { typeof(WorkItemStatusArbitraries) })]
    public void WhereActive_AllStatusValues_Property(WorkItemStatus status)
    {
        // Each property run uses a fresh isolated database
        var options = new DbContextOptionsBuilder<PipelineDbContext>()
            .UseInMemoryDatabase(databaseName: $"WhereActive_Prop_{Guid.NewGuid()}")
            .Options;

        using var db = new InMemoryPipelineDbContext(options);
        db.WorkItems.Add(MakeWorkItem(status));
        db.SaveChanges();

        var results = db.WorkItems.WhereActive().ToList();

        var shouldBeActive =
            status == WorkItemStatus.Dispatched ||
            status == WorkItemStatus.Running;

        if (shouldBeActive)
        {
            if (results.Count != 1)
                throw new Exception(
                    $"WhereActive() should return 1 item for Status={status} but returned {results.Count}.");
        }
        else
        {
            if (results.Count != 0)
                throw new Exception(
                    $"WhereActive() should return 0 items for Status={status} but returned {results.Count}.");
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────

    private async Task SeedAsync(WorkItemStatus status)
    {
        await using var db = new InMemoryPipelineDbContext(_options);
        db.WorkItems.Add(MakeWorkItem(status));
        await db.SaveChangesAsync();
    }

    private static WorkItemEntity MakeWorkItem(WorkItemStatus status) => new()
    {
        Id = Guid.NewGuid(),
        IssueIdentifier = $"owner/repo#{(int)status}",
        IssueProviderConfigId = "issue-cfg-1",
        Status = status,
        TaskType = WorkItemTaskType.Implementation,
        AgentSelector = "kiro,dotnet",
        CreatedAt = DateTimeOffset.UtcNow,
        Payload = "{}"
    };

    public void Dispose()
    {
        GC.SuppressFinalize(this);
    }

    // ── InMemory EF context ───────────────────────────────────────────────

    private sealed class InMemoryPipelineDbContext : PipelineDbContext
    {
        public InMemoryPipelineDbContext(DbContextOptions<PipelineDbContext> options)
            : base(options) { }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);
            // Disable RowVersion concurrency token — InMemory provider doesn't support xmin
            foreach (var entityType in modelBuilder.Model.GetEntityTypes())
            {
                var rowVersionProp = entityType.FindProperty("RowVersion");
                if (rowVersionProp != null)
                {
                    rowVersionProp.IsConcurrencyToken = false;
                    rowVersionProp.ValueGenerated = Microsoft.EntityFrameworkCore.Metadata.ValueGenerated.Never;
                }
            }
            // Remove filtered partial indexes — InMemory provider doesn't support HasFilter()
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
}

/// <summary>
/// FsCheck arbitrary generator for <see cref="WorkItemStatus"/> values.
/// Generates all six possible enum values with equal probability.
/// </summary>
public class WorkItemStatusArbitraries
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
