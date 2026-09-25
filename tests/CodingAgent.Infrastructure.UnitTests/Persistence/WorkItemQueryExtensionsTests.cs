using AwesomeAssertions;
using CodingAgent.Infrastructure.Persistence;
using CodingAgent.Infrastructure.Persistence.Entities;
using CodingAgent.Pipeline.Models;
using FsCheck;
using FsCheck.Fluent;
using FsCheck.Xunit;
using Microsoft.EntityFrameworkCore;

namespace CodingAgent.Infrastructure.UnitTests.Persistence;

/// <summary>
/// Unit tests for <see cref="WorkItemQueryExtensions.WhereActive"/> and
/// <see cref="WorkItemQueryExtensions.WhereActiveOrRecentlyTerminal"/>.
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

    // ── WhereActive: Inclusion tests ──────────────────────────────────────

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

    // ── WhereActive: Exclusion tests ──────────────────────────────────────

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

    // ── WhereActive: Mixed-set test ───────────────────────────────────────

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

    // ── WhereActive: EF Core translatability ─────────────────────────────

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

    // ── WhereActive: Property-based test ─────────────────────────────────

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

    // ── WhereActiveOrRecentlyTerminal: Active-status inclusion ────────────

    /// <summary>
    /// All three active statuses (Pending, Dispatched, Running) are included regardless of cutoff.
    /// </summary>
    [Theory]
    [InlineData(WorkItemStatus.Pending)]
    [InlineData(WorkItemStatus.Dispatched)]
    [InlineData(WorkItemStatus.Running)]
    public async Task WhereActiveOrRecentlyTerminal_IncludesActiveStatuses(WorkItemStatus status)
    {
        var cutoff = DateTimeOffset.UtcNow.AddMinutes(-5);
        await SeedAsync(status);
        await using var db = new InMemoryPipelineDbContext(_options);

        var results = await db.WorkItems
            .WhereActiveOrRecentlyTerminal(cutoff)
            .ToListAsync();

        results.Should().ContainSingle(w => w.Status == status);
    }

    // ── WhereActiveOrRecentlyTerminal: Terminal inside cooldown ───────────

    /// <summary>
    /// Terminal items with CompletedAt after the cutoff are included.
    /// </summary>
    [Theory]
    [InlineData(WorkItemStatus.Succeeded)]
    [InlineData(WorkItemStatus.Failed)]
    [InlineData(WorkItemStatus.Cancelled)]
    public async Task WhereActiveOrRecentlyTerminal_IncludesTerminalItemsInsideCooldown(WorkItemStatus status)
    {
        var cutoff = DateTimeOffset.UtcNow.AddMinutes(-5);
        var completedAt = DateTimeOffset.UtcNow.AddMinutes(-2); // inside cooldown
        await SeedAsync(status, completedAt);
        await using var db = new InMemoryPipelineDbContext(_options);

        var results = await db.WorkItems
            .WhereActiveOrRecentlyTerminal(cutoff)
            .ToListAsync();

        results.Should().ContainSingle(w => w.Status == status);
    }

    /// <summary>
    /// Terminal items with CompletedAt exactly at the cutoff (inclusive boundary) are included.
    /// </summary>
    [Fact]
    public async Task WhereActiveOrRecentlyTerminal_IncludesTerminalItemAtCutoffBoundary()
    {
        var cutoff = DateTimeOffset.UtcNow.AddMinutes(-5);
        await SeedAsync(WorkItemStatus.Succeeded, completedAt: cutoff);
        await using var db = new InMemoryPipelineDbContext(_options);

        var results = await db.WorkItems
            .WhereActiveOrRecentlyTerminal(cutoff)
            .ToListAsync();

        results.Should().ContainSingle();
    }

    // ── WhereActiveOrRecentlyTerminal: Terminal outside cooldown ──────────

    /// <summary>
    /// Terminal items with CompletedAt before the cutoff are excluded.
    /// </summary>
    [Theory]
    [InlineData(WorkItemStatus.Succeeded)]
    [InlineData(WorkItemStatus.Failed)]
    [InlineData(WorkItemStatus.Cancelled)]
    public async Task WhereActiveOrRecentlyTerminal_ExcludesTerminalItemsOutsideCooldown(WorkItemStatus status)
    {
        var cutoff = DateTimeOffset.UtcNow.AddMinutes(-5);
        var completedAt = DateTimeOffset.UtcNow.AddMinutes(-10); // outside cooldown
        await SeedAsync(status, completedAt);
        await using var db = new InMemoryPipelineDbContext(_options);

        var results = await db.WorkItems
            .WhereActiveOrRecentlyTerminal(cutoff)
            .ToListAsync();

        results.Should().BeEmpty();
    }

    // ── WhereActiveOrRecentlyTerminal: Terminal with null CompletedAt ─────

    /// <summary>
    /// Terminal items with a null CompletedAt are excluded — the null guard prevents them
    /// from being matched by the recently-terminal branch.
    /// </summary>
    [Theory]
    [InlineData(WorkItemStatus.Succeeded)]
    [InlineData(WorkItemStatus.Failed)]
    [InlineData(WorkItemStatus.Cancelled)]
    public async Task WhereActiveOrRecentlyTerminal_ExcludesTerminalItemsWithNullCompletedAt(WorkItemStatus status)
    {
        var cutoff = DateTimeOffset.UtcNow.AddMinutes(-5);
        await SeedAsync(status, completedAt: null);
        await using var db = new InMemoryPipelineDbContext(_options);

        var results = await db.WorkItems
            .WhereActiveOrRecentlyTerminal(cutoff)
            .ToListAsync();

        results.Should().BeEmpty();
    }

    // ── WhereActiveOrRecentlyTerminal: Mixed-set ──────────────────────────

    [Fact]
    public async Task WhereActiveOrRecentlyTerminal_OnlyReturnsMatchingFromMixedSet()
    {
        var cutoff = DateTimeOffset.UtcNow.AddMinutes(-5);

        await using (var seedDb = new InMemoryPipelineDbContext(_options))
        {
            // Active — should be included
            seedDb.WorkItems.Add(MakeWorkItem(WorkItemStatus.Pending));
            seedDb.WorkItems.Add(MakeWorkItem(WorkItemStatus.Dispatched));
            seedDb.WorkItems.Add(MakeWorkItem(WorkItemStatus.Running));
            // Recently terminal — should be included
            seedDb.WorkItems.Add(MakeWorkItem(WorkItemStatus.Succeeded, completedAt: DateTimeOffset.UtcNow.AddMinutes(-2)));
            // Stale terminal — should be excluded
            seedDb.WorkItems.Add(MakeWorkItem(WorkItemStatus.Failed, completedAt: DateTimeOffset.UtcNow.AddMinutes(-10)));
            // Terminal with null CompletedAt — should be excluded
            seedDb.WorkItems.Add(MakeWorkItem(WorkItemStatus.Cancelled, completedAt: null));
            await seedDb.SaveChangesAsync();
        }

        await using var db = new InMemoryPipelineDbContext(_options);
        var results = await db.WorkItems.WhereActiveOrRecentlyTerminal(cutoff).ToListAsync();

        results.Should().HaveCount(4);
        results.Should().Contain(w => w.Status == WorkItemStatus.Pending);
        results.Should().Contain(w => w.Status == WorkItemStatus.Dispatched);
        results.Should().Contain(w => w.Status == WorkItemStatus.Running);
        results.Should().Contain(w => w.Status == WorkItemStatus.Succeeded);
    }

    // ── WhereActiveOrRecentlyTerminal: Property-based test ────────────────

    /// <summary>
    /// Property: for any status + completedAt combination, WhereActiveOrRecentlyTerminal returns
    /// the item iff it is in an active status OR (terminal AND CompletedAt &gt;= cutoff).
    /// </summary>
    [Property(MaxTest = 20, Arbitrary = new[] { typeof(WorkItemStatusArbitraries) })]
    public void WhereActiveOrRecentlyTerminal_MatchesExpectedPredicate_Property(WorkItemStatus status)
    {
        var cutoff = DateTimeOffset.UtcNow.AddMinutes(-5);
        // Use inside-cooldown and outside-cooldown for terminal statuses
        var completedAtCases = new DateTimeOffset?[]
        {
            null,
            DateTimeOffset.UtcNow.AddMinutes(-2),  // inside cooldown
            DateTimeOffset.UtcNow.AddMinutes(-10), // outside cooldown
        };

        foreach (var completedAt in completedAtCases)
        {
            var options = new DbContextOptionsBuilder<PipelineDbContext>()
                .UseInMemoryDatabase(databaseName: $"WhereActiveOrRecentlyTerminal_Prop_{Guid.NewGuid()}")
                .Options;

            using var db = new InMemoryPipelineDbContext(options);
            db.WorkItems.Add(MakeWorkItem(status, completedAt));
            db.SaveChanges();

            var results = db.WorkItems.WhereActiveOrRecentlyTerminal(cutoff).ToList();

            var activeStatuses = PipelineConstants.ActiveWorkItemStatuses;
            var shouldMatch = activeStatuses.Contains(status) ||
                              (completedAt.HasValue && completedAt.Value >= cutoff);

            if (shouldMatch && results.Count != 1)
                throw new Exception(
                    $"WhereActiveOrRecentlyTerminal should include Status={status}, CompletedAt={completedAt} but returned {results.Count} items.");
            if (!shouldMatch && results.Count != 0)
                throw new Exception(
                    $"WhereActiveOrRecentlyTerminal should exclude Status={status}, CompletedAt={completedAt} but returned {results.Count} items.");
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────

    private async Task SeedAsync(WorkItemStatus status, DateTimeOffset? completedAt = null)
    {
        await using var db = new InMemoryPipelineDbContext(_options);
        db.WorkItems.Add(MakeWorkItem(status, completedAt));
        await db.SaveChangesAsync();
    }

    private static WorkItemEntity MakeWorkItem(WorkItemStatus status, DateTimeOffset? completedAt = null) => new()
    {
        Id = Guid.NewGuid(),
        IssueIdentifier = $"owner/repo#{(int)status}",
        IssueProviderConfigId = "issue-cfg-1",
        Status = status,
        TaskType = WorkItemTaskType.Implementation,
        AgentSelector = "kiro,dotnet",
        CreatedAt = DateTimeOffset.UtcNow,
        Payload = "{}",
        CompletedAt = completedAt
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
