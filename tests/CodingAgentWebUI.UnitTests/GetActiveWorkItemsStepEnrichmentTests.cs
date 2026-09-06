using AwesomeAssertions;
using CodingAgentWebUI.Api;
using CodingAgentWebUI.Infrastructure.Persistence;
using CodingAgentWebUI.Infrastructure.Persistence.Entities;
using CodingAgentWebUI.Orchestration;
using CodingAgentWebUI.Pipeline.Models;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Moq;
using ILogger = Serilog.ILogger;

namespace CodingAgentWebUI.UnitTests;

/// <summary>
/// Unit tests for the step-enrichment path of <see cref="WorkItemEndpoints.GetActiveWorkItems"/>.
/// Calls the internal handler directly (InternalsVisibleTo granted in CodingAgentWebUI.Api.csproj)
/// to verify that <see cref="ActiveWorkItemDto.CurrentStep"/> is populated from
/// <see cref="IOrchestratorRunService"/> when a live run is registered, and null when it is not.
/// </summary>
public sealed class GetActiveWorkItemsStepEnrichmentTests : IDisposable
{
    private readonly DbContextOptions<PipelineDbContext> _dbOptions;
    private readonly InMemoryDbContextFactory _dbFactory;

    public GetActiveWorkItemsStepEnrichmentTests()
    {
        _dbOptions = new DbContextOptionsBuilder<PipelineDbContext>()
            .UseInMemoryDatabase($"StepEnrichment-{Guid.NewGuid()}")
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;

        using (var ctx = new InMemoryPipelineDbContext(_dbOptions))
            ctx.Database.EnsureCreated();

        _dbFactory = new InMemoryDbContextFactory(_dbOptions);
    }

    public void Dispose()
    {
        using var db = new InMemoryPipelineDbContext(_dbOptions);
        db.Database.EnsureDeleted();
    }

    // ── Test 1: enriched path ─────────────────────────────────────────────

    // TODO: Only one work item is seeded, so the test cannot distinguish "enriched by RunId match"
    // from "enriched by coincidence". Consider seeding a second Running item with no live run and
    // asserting it has CurrentStep == null, making the per-ID key-matching logic directly observable.
    // (TestQualityReviewer review, line 48)
    [Fact]
    public async Task GetActiveWorkItems_PopulatesCurrentStep_WhenLiveRunExists()
    {
        // Arrange — seed a Running WorkItem whose GUID will be used as the run key
        var workItemId = Guid.NewGuid();
        await SeedWorkItemAsync(workItemId, WorkItemStatus.Running);

        // Register a live run for that item with a non-default step so we can assert the value was
        // taken from the run (not a zero-initialisation coincidence).
        var runService = new OrchestratorRunService(new Mock<ILogger>().Object);
        var run = PipelineRun.CreateImplementation(new PipelineRunCreationParams
        {
            RunId = workItemId.ToString(),
            IssueIdentifier = "org/repo#42",
            IssueTitle = "Add step column",
            IssueProviderConfigId = "ip-test",
            RepoProviderConfigId = "rp-test",
            InitiatedBy = "test"
        });
        run.CurrentStep = PipelineStep.RunningQualityGates; // explicit non-default step
        runService.AddRun(run);

        // Act
        var result = await WorkItemEndpoints.GetActiveWorkItems(
            olderThanSeconds: 0,
            dbFactory: _dbFactory,
            runService: runService,
            projectId: null,
            ct: default);

        // Assert
        var ok = result.Should().BeOfType<Ok<IReadOnlyList<ActiveWorkItemDto>>>().Subject;
        var items = ok.Value!;
        items.Should().ContainSingle();
        var item = items.Single();
        item.Id.Should().Be(workItemId);
        item.CurrentStep.Should().Be(PipelineStep.RunningQualityGates,
            "the step must be taken from the registered live run");
    }

    // ── Test 2: unenriched path ───────────────────────────────────────────

    // TODO: The null runService path (if (runService is not null) guard in the handler) is not covered.
    // Add a third test passing runService: null and asserting CurrentStep == null to lock in the
    // defensive-null behaviour required by the acceptance criteria. (Correctness review, line 90 /
    // TestQualityReviewer review, line 44)
    // TODO: This test does not assert item.Id == workItemId, unlike Test 1. Add the identity assertion
    // to guard against a stale DB row surfacing from a prior test run. (TestQualityReviewer review, line 93)
    [Fact]
    public async Task GetActiveWorkItems_CurrentStepIsNull_WhenNoLiveRunExists()
    {
        // Arrange — seed a Running WorkItem but register NO live run
        var workItemId = Guid.NewGuid();
        await SeedWorkItemAsync(workItemId, WorkItemStatus.Running);

        var emptyRunService = new OrchestratorRunService(new Mock<ILogger>().Object);

        // Act
        var result = await WorkItemEndpoints.GetActiveWorkItems(
            olderThanSeconds: 0,
            dbFactory: _dbFactory,
            runService: emptyRunService,
            projectId: null,
            ct: default);

        // Assert
        var ok = result.Should().BeOfType<Ok<IReadOnlyList<ActiveWorkItemDto>>>().Subject;
        var items = ok.Value!;
        items.Should().ContainSingle();
        items.Single().CurrentStep.Should().BeNull(
            "no live run is registered, so CurrentStep must degrade gracefully to null");
    }

    // ── Helpers ───────────────────────────────────────────────────────────

    private async Task SeedWorkItemAsync(Guid id, WorkItemStatus status)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var dispatchedAt = DateTimeOffset.UtcNow.AddMinutes(-5);
        db.WorkItems.Add(new WorkItemEntity
        {
            Id = id,
            TaskType = WorkItemTaskType.Implementation,
            IssueIdentifier = "org/repo#42",
            IssueProviderConfigId = "ip-test",
            Status = status,
            AgentSelector = "",
            CreatedAt = dispatchedAt,
            DispatchedAt = dispatchedAt,
            TimeoutSeconds = 3600
        });
        await db.SaveChangesAsync();
    }

    // ── Test infrastructure ───────────────────────────────────────────────

    private sealed class InMemoryPipelineDbContext : PipelineDbContext
    {
        public InMemoryPipelineDbContext(DbContextOptions<PipelineDbContext> options) : base(options) { }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);
            // Shim 1: InMemory provider does not support RowVersion concurrency tokens.
            foreach (var entityType in modelBuilder.Model.GetEntityTypes())
            {
                var rowVersionProp = entityType.FindProperty("RowVersion");
                if (rowVersionProp != null)
                {
                    rowVersionProp.IsConcurrencyToken = false;
                    rowVersionProp.ValueGenerated = Microsoft.EntityFrameworkCore.Metadata.ValueGenerated.Never;
                }
            }
            // Shim 2: InMemory provider does not support filtered (partial) indexes.
            foreach (var entityType in modelBuilder.Model.GetEntityTypes())
            {
                var filteredIndexes = entityType.GetIndexes().Where(i => i.GetFilter() != null).ToList();
                foreach (var index in filteredIndexes)
                    entityType.RemoveIndex(index);
            }
        }
    }

    private sealed class InMemoryDbContextFactory : IDbContextFactory<PipelineDbContext>
    {
        private readonly DbContextOptions<PipelineDbContext> _options;
        public InMemoryDbContextFactory(DbContextOptions<PipelineDbContext> options) => _options = options;
        public PipelineDbContext CreateDbContext() => new InMemoryPipelineDbContext(_options);
        public Task<PipelineDbContext> CreateDbContextAsync(CancellationToken ct = default)
            => Task.FromResult(CreateDbContext());
    }
}
