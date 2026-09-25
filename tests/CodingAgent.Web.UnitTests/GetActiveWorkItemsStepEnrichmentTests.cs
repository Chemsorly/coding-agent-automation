using AwesomeAssertions;
using CodingAgent.Api;
using CodingAgent.Infrastructure.Persistence;
using CodingAgent.Infrastructure.Persistence.Entities;
using CodingAgent.Orchestration;
using CodingAgent.Pipeline;
using CodingAgent.Pipeline.Models;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Moq;
using ILogger = Serilog.ILogger;

namespace CodingAgent.Web.UnitTests;

/// <summary>
/// Unit tests for the <see cref="WorkItemQueryEndpoints.GetActiveWorkItems"/> handler's
/// <see cref="ActiveWorkItemDto.CurrentStep"/> enrichment logic.
/// Tests call the internal handler directly to avoid full host-build overhead
/// (InternalsVisibleTo("CodingAgent.Web.UnitTests") is set in the Api csproj).
/// </summary>
public sealed class GetActiveWorkItemsStepEnrichmentTests : IDisposable
{
    private readonly DbContextOptions<PipelineDbContext> _dbOptions;
    private readonly InMemoryDbContextFactory _dbFactory;

    public GetActiveWorkItemsStepEnrichmentTests()
    {
        _dbOptions = new DbContextOptionsBuilder<PipelineDbContext>()
            .UseInMemoryDatabase($"ActiveWorkItemsStepEnrichment-{Guid.NewGuid()}")
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;

        using var ctx = new InMemoryPipelineDbContext(_dbOptions);
        ctx.Database.EnsureCreated();

        _dbFactory = new InMemoryDbContextFactory(_dbOptions);
    }

    public void Dispose()
    {
        using var db = new InMemoryPipelineDbContext(_dbOptions);
        db.Database.EnsureDeleted();
    }

    // ── Helpers ───────────────────────────────────────────────────────────

    private async Task<Guid> SeedRunningWorkItemAsync()
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var id = Guid.NewGuid();
        db.WorkItems.Add(new WorkItemEntity
        {
            Id = id,
            Status = WorkItemStatus.Running,
            TaskType = WorkItemTaskType.Implementation,
            IssueIdentifier = $"owner/repo#{id:N}",
            AgentSelector = "kiro",
            DispatchedAt = DateTimeOffset.UtcNow.AddMinutes(-5),
            CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-6),
            TimeoutSeconds = 3600
        });
        await db.SaveChangesAsync();
        return id;
    }

    private static OrchestratorRunService CreateRunService() =>
        new(new Mock<ILogger>().Object);

    private static PipelineRun CreateLiveRun(Guid workItemId, PipelineStep step)
    {
        var run = PipelineRun.CreateImplementation(new PipelineRunCreationParams
        {
            RunId = workItemId.ToString(),
            IssueIdentifier = new IssueIdentifier($"owner/repo#{workItemId:N}"),
            IssueTitle = "Test issue",
            IssueProviderConfigId = "ip-1",
            RepoProviderConfigId = "rp-1",
            InitiatedBy = "test"
        });
        // TODO: Direct assignment bypasses PipelineRun.TransitionTo state machine. If TransitionTo
        // has observable side-effects (HighestStep, telemetry, timestamps), this run is in a state
        // that cannot arise in production. Acceptable for enrichment plumbing tests, but if
        // PipelineRun state machine semantics are ever enforced on CurrentStep (e.g. via
        // init-only or computed property), this assignment will need to use TransitionTo instead.
        run.CurrentStep = step;
        return run;
    }

    // ── Tests ─────────────────────────────────────────────────────────────

    /// <summary>
    /// When a live run is registered in <see cref="OrchestratorRunService"/> for a Running
    /// work item, <see cref="ActiveWorkItemDto.CurrentStep"/> is populated from it.
    /// </summary>
    [Fact]
    public async Task GetActiveWorkItems_PopulatesCurrentStep_WhenLiveRunExists()
    {
        // Arrange
        var workItemId = await SeedRunningWorkItemAsync();
        var runService = CreateRunService();
        var liveRun = CreateLiveRun(workItemId, PipelineStep.RunningQualityGates);
        runService.AddRun(liveRun);

        // Act — call the internal handler directly (olderThanSeconds=0 → cutoff=now, excludes all)
        // Use negative value so cutoff is in the future, ensuring our item is included.
        // TODO: Using a negative olderThanSeconds to bypass the time filter is fragile — if the
        // endpoint ever guards against negative values (e.g. Math.Max(0, olderThanSeconds)), all
        // three tests in this class will silently return empty lists and ContainSingle() will
        // throw a confusing error. A more robust approach: seed DispatchedAt sufficiently in the
        // past (e.g. AddMinutes(-90)) and pass a positive olderThanSeconds that the seeded item
        // actually satisfies, exercising the real filter path. Applies to all three tests below.
        var result = await WorkItemQueryEndpoints.GetActiveWorkItems(
            olderThanSeconds: -3600,
            dbFactory: _dbFactory,
            runService: runService,
            projectId: null,
            ct: CancellationToken.None);

        // Assert
        var ok = result.Should().BeOfType<Ok<IReadOnlyList<ActiveWorkItemDto>>>().Subject;
        var item = ok.Value.Should().ContainSingle().Subject;
        item.Id.Should().Be(workItemId);
        item.CurrentStep.Should().Be(PipelineStep.RunningQualityGates);
    }

    /// <summary>
    /// When no live run is registered for a Running work item,
    /// <see cref="ActiveWorkItemDto.CurrentStep"/> is null (graceful degradation).
    /// </summary>
    [Fact]
    public async Task GetActiveWorkItems_CurrentStepIsNull_WhenNoLiveRunRegistered()
    {
        // Arrange — run service present but no run registered for this work item
        var workItemId = await SeedRunningWorkItemAsync();
        var runService = CreateRunService(); // empty — no runs added

        // Act
        var result = await WorkItemQueryEndpoints.GetActiveWorkItems(
            olderThanSeconds: -3600,
            dbFactory: _dbFactory,
            runService: runService,
            projectId: null,
            ct: CancellationToken.None);

        // Assert
        var ok = result.Should().BeOfType<Ok<IReadOnlyList<ActiveWorkItemDto>>>().Subject;
        var item = ok.Value.Should().ContainSingle().Subject;
        item.Id.Should().Be(workItemId);
        item.CurrentStep.Should().BeNull();
    }

    /// <summary>
    /// When the run service is null (e.g. a test scenario without DI injection),
    /// the endpoint returns items without enrichment — no exception thrown.
    /// </summary>
    [Fact]
    public async Task GetActiveWorkItems_CurrentStepIsNull_WhenRunServiceIsNull()
    {
        // Arrange
        var workItemId = await SeedRunningWorkItemAsync();

        // Act — pass null explicitly for the optional runService
        var result = await WorkItemQueryEndpoints.GetActiveWorkItems(
            olderThanSeconds: -3600,
            dbFactory: _dbFactory,
            runService: null,
            projectId: null,
            ct: CancellationToken.None);

        // Assert
        var ok = result.Should().BeOfType<Ok<IReadOnlyList<ActiveWorkItemDto>>>().Subject;
        var item = ok.Value.Should().ContainSingle().Subject;
        item.Id.Should().Be(workItemId);
        item.CurrentStep.Should().BeNull();
    }

    // ── IssueUrl enrichment tests (issue #2947) ───────────────────────────

    // TODO [WARNING]: No test covers the IsAllowedUrl security guard in WorkItemQueryEndpoints.cs.
    // A payload containing a non-http/https URL (e.g. "javascript:alert(1)", "ftp://malicious",
    // "data:text/html,...") should result in IssueUrl = null in the returned DTO. Without this test,
    // a regression that removes or weakens the IsAllowedUrl check would not be detected.
    // Add a parameterised test (or multiple Theory cases) seeding a work item with each disallowed
    // scheme and asserting that ActiveWorkItemDto.IssueUrl is null.

    /// <summary>
    /// When a work item's Payload contains an IssueDetail with a Url, GetActiveWorkItems
    /// must populate ActiveWorkItemDto.IssueUrl so the Work page can link the issue number
    /// directly to the provider without a separate API call.
    /// </summary>
    [Fact]
    public async Task GetActiveWorkItems_WithPayloadHavingIssueUrl_PopulatesIssueUrl()
    {
        // Arrange
        const string expectedIssueUrl = "https://github.com/owner/repo/issues/42";
        var workItemId = await SeedRunningWorkItemWithPayloadAsync(issueUrl: expectedIssueUrl);

        // Act
        var result = await WorkItemQueryEndpoints.GetActiveWorkItems(
            olderThanSeconds: -3600,
            dbFactory: _dbFactory,
            runService: null,
            projectId: null,
            ct: CancellationToken.None);

        // Assert
        var ok = result.Should().BeOfType<Ok<IReadOnlyList<ActiveWorkItemDto>>>().Subject;
        var item = ok.Value.Should().ContainSingle().Subject;
        item.Id.Should().Be(workItemId);
        item.IssueUrl.Should().Be(expectedIssueUrl);
    }

    /// <summary>
    /// When a work item's Payload contains an IssueDetail with no Url (null), GetActiveWorkItems
    /// must return IssueUrl = null — null is a valid absence-of-URL signal, not an error.
    /// </summary>
    [Fact]
    public async Task GetActiveWorkItems_WithPayloadHavingNoIssueUrl_IssueUrlIsNull()
    {
        // Arrange — payload present but IssueDetail.Url is null
        var workItemId = await SeedRunningWorkItemWithPayloadAsync(issueUrl: null);

        // Act
        var result = await WorkItemQueryEndpoints.GetActiveWorkItems(
            olderThanSeconds: -3600,
            dbFactory: _dbFactory,
            runService: null,
            projectId: null,
            ct: CancellationToken.None);

        // Assert
        var ok = result.Should().BeOfType<Ok<IReadOnlyList<ActiveWorkItemDto>>>().Subject;
        var item = ok.Value.Should().ContainSingle().Subject;
        item.Id.Should().Be(workItemId);
        item.IssueUrl.Should().BeNull();
    }

    /// <summary>
    /// When a work item has no Payload (null), IssueUrl must be null — no exception thrown
    /// and the item is still returned.
    /// </summary>
    [Fact]
    public async Task GetActiveWorkItems_WithNullPayload_IssueUrlIsNull()
    {
        // Arrange — base SeedRunningWorkItemAsync creates items with no payload
        var workItemId = await SeedRunningWorkItemAsync();

        // Act
        var result = await WorkItemQueryEndpoints.GetActiveWorkItems(
            olderThanSeconds: -3600,
            dbFactory: _dbFactory,
            runService: null,
            projectId: null,
            ct: CancellationToken.None);

        // Assert
        var ok = result.Should().BeOfType<Ok<IReadOnlyList<ActiveWorkItemDto>>>().Subject;
        var item = ok.Value.Should().ContainSingle().Subject;
        item.Id.Should().Be(workItemId);
        item.IssueUrl.Should().BeNull();
    }

    // ── Inner helpers — shared InMemory EF infrastructure ─────────────────

    private async Task<Guid> SeedRunningWorkItemWithPayloadAsync(string? issueUrl)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var id = Guid.NewGuid();
        var payload = System.Text.Json.JsonSerializer.Serialize(new JobDistributionRequest
        {
            IssueIdentifier = new IssueIdentifier($"owner/repo#{id:N}"),
            IssueProviderConfigId = "ip-1",
            RepoProviderConfigId = "rp-1",
            InitiatedBy = "test",
            TaskType = WorkItemTaskType.Implementation,
            AgentSelector = "kiro",
            TimeoutSeconds = 3600,
            IssueDetail = new IssueDetail
            {
                Description = "test",
                Identifier = $"{id:N}",
                Labels = System.Array.Empty<string>(),
                Title = "Test issue with URL",
                Url = issueUrl,
            }
        }, CodingAgent.Pipeline.PipelineJsonOptions.Default);
        db.WorkItems.Add(new WorkItemEntity
        {
            Id = id,
            Status = WorkItemStatus.Running,
            TaskType = WorkItemTaskType.Implementation,
            IssueIdentifier = $"owner/repo#{id:N}",
            AgentSelector = "kiro",
            DispatchedAt = DateTimeOffset.UtcNow.AddMinutes(-5),
            CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-6),
            TimeoutSeconds = 3600,
            Payload = payload,
        });
        await db.SaveChangesAsync();
        return id;
    }

    private sealed class InMemoryPipelineDbContext : PipelineDbContext
    {
        public InMemoryPipelineDbContext(DbContextOptions<PipelineDbContext> options) : base(options) { }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);
            foreach (var et in modelBuilder.Model.GetEntityTypes())
            {
                var rv = et.FindProperty("RowVersion");
                if (rv != null)
                {
                    rv.IsConcurrencyToken = false;
                    rv.ValueGenerated = Microsoft.EntityFrameworkCore.Metadata.ValueGenerated.Never;
                }
            }
            foreach (var et in modelBuilder.Model.GetEntityTypes())
                foreach (var idx in et.GetIndexes().Where(i => i.GetFilter() != null).ToList())
                    et.RemoveIndex(idx);
        }
    }

    private sealed class InMemoryDbContextFactory : IDbContextFactory<PipelineDbContext>
    {
        private readonly DbContextOptions<PipelineDbContext> _options;

        public InMemoryDbContextFactory(DbContextOptions<PipelineDbContext> options) =>
            _options = options;

        public PipelineDbContext CreateDbContext() => new InMemoryPipelineDbContext(_options);

        public Task<PipelineDbContext> CreateDbContextAsync(CancellationToken ct = default) =>
            Task.FromResult(CreateDbContext());
    }
}
