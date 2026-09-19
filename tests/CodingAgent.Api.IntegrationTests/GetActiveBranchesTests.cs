using AwesomeAssertions;
using CodingAgent.Infrastructure.Persistence;
using CodingAgent.Infrastructure.Persistence.Entities;
using CodingAgent.Pipeline.Models;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace CodingAgent.Api.IntegrationTests;

/// <summary>
/// Unit tests for <see cref="PipelineRunEndpoints.GetActiveBranches"/> (issue #2687).
///
/// Verifies that the endpoint queries WorkItems from the database rather than reading from
/// the in-memory _activeRuns collection, making it immune to ghost runs caused by lost
/// SignalR completion signals.
///
/// Tests call the internal static method directly using an InMemory DbContext so they are
/// fast and self-contained. No IOrchestratorRunService mock is needed — the new implementation
/// has no dependency on in-memory run state.
/// </summary>
public sealed class GetActiveBranchesTests
{
    // ── Helpers ───────────────────────────────────────────────────────────────

    private static DbContextOptions<PipelineDbContext> CreateDbOptions()
        => new DbContextOptionsBuilder<PipelineDbContext>()
            .UseInMemoryDatabase($"GetActiveBranches-{Guid.NewGuid():N}")
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;

    private static async Task<WorkItemEntity> SeedWorkItemAsync(
        DbContextOptions<PipelineDbContext> opts,
        WorkItemStatus status,
        string? branchName = null)
    {
        var item = new WorkItemEntity
        {
            Id = Guid.NewGuid(),
            IssueIdentifier = $"org/repo#{Guid.NewGuid():N}",
            IssueProviderConfigId = "ip-1",
            Status = status,
            TaskType = WorkItemTaskType.Implementation,
            AgentSelector = "kiro",
            CreatedAt = DateTimeOffset.UtcNow,
            BranchName = branchName
        };

        await using var ctx = new TestDbContext(opts);
        ctx.Database.EnsureCreated();
        ctx.WorkItems.Add(item);
        await ctx.SaveChangesAsync();
        return item;
    }

    private static IDbContextFactory<PipelineDbContext> CreateDbFactory(DbContextOptions<PipelineDbContext> opts)
        => new TestDbContextFactory(opts);

    // ── Acceptance criterion #5 — endpoint returns empty when WorkItems are terminal ─────────

    /// <summary>
    /// Acceptance criterion #5: GET /api/pipeline-runs/active-branches returns an empty list
    /// when all WorkItems with BranchName set are in terminal state.
    ///
    /// This is structurally immune to ghost runs: the implementation queries the DB and
    /// never touches in-memory _activeRuns state. The "even if _activeRuns still contains
    /// entries" clause is satisfied by the fact that no IOrchestratorRunService is involved at all.
    /// </summary>
    [Fact]
    public async Task GetActiveBranches_AllWorkItemsTerminal_ReturnsEmptyList()
    {
        // Arrange: seed a Succeeded WorkItem with a BranchName
        var opts = CreateDbOptions();
        await SeedWorkItemAsync(opts, WorkItemStatus.Succeeded, branchName: "feature/auto-42-fix");
        var dbFactory = CreateDbFactory(opts);

        // Act
        var result = await PipelineRunEndpoints.GetActiveBranches(dbFactory, CancellationToken.None);

        // Assert: terminal WorkItem must not appear in active branches
        var ok = result.Should().BeOfType<Ok<IReadOnlyList<string>>>().Subject;
        ok.Value.Should().BeEmpty(
            "a Succeeded WorkItem must not appear in active-branches even though it has a BranchName set");
    }

    /// <summary>
    /// Verifies with Failed status — another terminal state.
    /// </summary>
    [Fact]
    public async Task GetActiveBranches_FailedWorkItem_ReturnsEmptyList()
    {
        var opts = CreateDbOptions();
        await SeedWorkItemAsync(opts, WorkItemStatus.Failed, branchName: "feature/failed-run");
        var dbFactory = CreateDbFactory(opts);

        var result = await PipelineRunEndpoints.GetActiveBranches(dbFactory, CancellationToken.None);

        var ok = result.Should().BeOfType<Ok<IReadOnlyList<string>>>().Subject;
        ok.Value.Should().BeEmpty("a Failed WorkItem must not appear in active-branches");
    }

    /// <summary>
    /// Verifies with Cancelled status — another terminal state.
    /// </summary>
    [Fact]
    public async Task GetActiveBranches_CancelledWorkItem_ReturnsEmptyList()
    {
        var opts = CreateDbOptions();
        await SeedWorkItemAsync(opts, WorkItemStatus.Cancelled, branchName: "feature/cancelled-run");
        var dbFactory = CreateDbFactory(opts);

        var result = await PipelineRunEndpoints.GetActiveBranches(dbFactory, CancellationToken.None);

        var ok = result.Should().BeOfType<Ok<IReadOnlyList<string>>>().Subject;
        ok.Value.Should().BeEmpty("a Cancelled WorkItem must not appear in active-branches");
    }

    // ── Acceptance criterion #6 — Succeeded WorkItem does NOT appear ──────────────────────

    /// <summary>
    /// Acceptance criterion #6: a run whose WorkItem.Status = Succeeded must NOT appear in
    /// the active-branches response, even when a Running WorkItem with a different BranchName exists.
    /// </summary>
    [Fact]
    public async Task GetActiveBranches_MixedStatuses_OnlyActiveRunningBranchReturned()
    {
        // Arrange: one Running WorkItem and one Succeeded WorkItem, both with BranchNames
        var opts = CreateDbOptions();
        await SeedWorkItemAsync(opts, WorkItemStatus.Running, branchName: "feature/active-branch");
        await SeedWorkItemAsync(opts, WorkItemStatus.Succeeded, branchName: "feature/done-branch");
        var dbFactory = CreateDbFactory(opts);

        // Act
        var result = await PipelineRunEndpoints.GetActiveBranches(dbFactory, CancellationToken.None);

        // Assert
        var ok = result.Should().BeOfType<Ok<IReadOnlyList<string>>>().Subject;
        ok.Value.Should().ContainSingle("only the Running WorkItem's branch should be returned")
            .Which.Should().Be("feature/active-branch");
        ok.Value.Should().NotContain("feature/done-branch",
            "a Succeeded WorkItem must not appear in active-branches");
    }

    // ── Active WorkItems with BranchName ARE returned ─────────────────────────────────────

    [Fact]
    public async Task GetActiveBranches_RunningWorkItemWithBranchName_ReturnsBranch()
    {
        var opts = CreateDbOptions();
        await SeedWorkItemAsync(opts, WorkItemStatus.Running, branchName: "feature/my-branch");
        var dbFactory = CreateDbFactory(opts);

        var result = await PipelineRunEndpoints.GetActiveBranches(dbFactory, CancellationToken.None);

        var ok = result.Should().BeOfType<Ok<IReadOnlyList<string>>>().Subject;
        ok.Value.Should().ContainSingle().Which.Should().Be("feature/my-branch");
    }

    [Fact]
    public async Task GetActiveBranches_DispatchedWorkItemWithBranchName_ReturnsBranch()
    {
        var opts = CreateDbOptions();
        await SeedWorkItemAsync(opts, WorkItemStatus.Dispatched, branchName: "feature/dispatched-branch");
        var dbFactory = CreateDbFactory(opts);

        var result = await PipelineRunEndpoints.GetActiveBranches(dbFactory, CancellationToken.None);

        var ok = result.Should().BeOfType<Ok<IReadOnlyList<string>>>().Subject;
        ok.Value.Should().ContainSingle().Which.Should().Be("feature/dispatched-branch");
    }

    // ── Active WorkItem with null BranchName is excluded ─────────────────────────────────

    [Fact]
    public async Task GetActiveBranches_RunningWorkItemWithNullBranchName_NotReturned()
    {
        // Arrange: Running WorkItem without a BranchName (branch not created yet)
        var opts = CreateDbOptions();
        await SeedWorkItemAsync(opts, WorkItemStatus.Running, branchName: null);
        var dbFactory = CreateDbFactory(opts);

        var result = await PipelineRunEndpoints.GetActiveBranches(dbFactory, CancellationToken.None);

        var ok = result.Should().BeOfType<Ok<IReadOnlyList<string>>>().Subject;
        ok.Value.Should().BeEmpty("a Running WorkItem with null BranchName must not appear in active-branches");
    }

    // ── Empty DB ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task GetActiveBranches_NoWorkItems_ReturnsEmptyList()
    {
        var opts = CreateDbOptions();
        await using var ctx = new TestDbContext(opts);
        ctx.Database.EnsureCreated();
        var dbFactory = CreateDbFactory(opts);

        var result = await PipelineRunEndpoints.GetActiveBranches(dbFactory, CancellationToken.None);

        var ok = result.Should().BeOfType<Ok<IReadOnlyList<string>>>().Subject;
        ok.Value.Should().BeEmpty("no WorkItems means no active branches");
    }

    // ── Distinct deduplication ────────────────────────────────────────────────────────────

    [Fact]
    public async Task GetActiveBranches_MultipleRunningWorkItemsSameBranch_ReturnsDistinct()
    {
        // Arrange: two Running WorkItems sharing the same branch (e.g. retry scenario)
        var opts = CreateDbOptions();
        await SeedWorkItemAsync(opts, WorkItemStatus.Running, branchName: "feature/shared-branch");
        await SeedWorkItemAsync(opts, WorkItemStatus.Running, branchName: "feature/shared-branch");
        var dbFactory = CreateDbFactory(opts);

        var result = await PipelineRunEndpoints.GetActiveBranches(dbFactory, CancellationToken.None);

        var ok = result.Should().BeOfType<Ok<IReadOnlyList<string>>>().Subject;
        ok.Value.Should().ContainSingle(
            "Distinct() must deduplicate branches when multiple active WorkItems share the same branch name");
        ok.Value[0].Should().Be("feature/shared-branch");
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private sealed class TestDbContext : PipelineDbContext
    {
        public TestDbContext(DbContextOptions<PipelineDbContext> options) : base(options) { }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);

            // Disable RowVersion concurrency token and partial indexes — not supported by InMemory provider
            foreach (var entityType in modelBuilder.Model.GetEntityTypes())
            {
                var rv = entityType.FindProperty("RowVersion");
                if (rv != null)
                {
                    rv.IsConcurrencyToken = false;
                    rv.ValueGenerated = Microsoft.EntityFrameworkCore.Metadata.ValueGenerated.Never;
                }
            }

            foreach (var entityType in modelBuilder.Model.GetEntityTypes())
            {
                var indexes = entityType.GetIndexes()
                    .Where(i => i.GetFilter() != null)
                    .ToList();
                foreach (var idx in indexes)
                    entityType.RemoveIndex(idx);
            }
        }
    }

    private sealed class TestDbContextFactory : IDbContextFactory<PipelineDbContext>
    {
        private readonly DbContextOptions<PipelineDbContext> _opts;
        public TestDbContextFactory(DbContextOptions<PipelineDbContext> opts) => _opts = opts;
        public PipelineDbContext CreateDbContext() => new TestDbContext(_opts);
        public Task<PipelineDbContext> CreateDbContextAsync(CancellationToken ct = default)
            => Task.FromResult(CreateDbContext());
    }
}
