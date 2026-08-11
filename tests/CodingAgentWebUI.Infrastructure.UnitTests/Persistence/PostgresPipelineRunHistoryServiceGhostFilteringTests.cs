using AwesomeAssertions;
using CodingAgentWebUI.Infrastructure.Persistence;
using CodingAgentWebUI.Infrastructure.Persistence.Entities;
using CodingAgentWebUI.Infrastructure.Persistence.Services;
using CodingAgentWebUI.Pipeline;
using CodingAgentWebUI.Pipeline.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Moq;
using ILogger = Serilog.ILogger;

namespace CodingAgentWebUI.Infrastructure.UnitTests.Persistence;

/// <summary>
/// Tests for <see cref="PostgresPipelineRunHistoryService"/> ghost entry filtering:
/// consolidation runs with null or corrupt SummaryJson must be excluded from history
/// via the IssueProviderConfigId column discriminator introduced in issue #1918.
/// These tests live in the Infrastructure.UnitTests project so that coverlet includes
/// the Infrastructure assembly in its coverage report.
/// </summary>
public sealed class PostgresPipelineRunHistoryServiceGhostFilteringTests : IDisposable
{
    private readonly DbContextOptions<PipelineDbContext> _dbOptions;
    private readonly PostgresPipelineRunHistoryService _sut;

    public PostgresPipelineRunHistoryServiceGhostFilteringTests()
    {
        _dbOptions = new DbContextOptionsBuilder<PipelineDbContext>()
            .UseInMemoryDatabase(databaseName: $"GhostFilteringTests-{Guid.NewGuid()}")
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;

        using (var ctx = new TestPipelineDbContext(_dbOptions))
            ctx.Database.EnsureCreated();

        var factory = new TestDbContextFactory(_dbOptions);
        _sut = new PostgresPipelineRunHistoryService(factory, new Mock<ILogger>().Object);
    }

    public void Dispose()
    {
        using var db = new TestPipelineDbContext(_dbOptions);
        db.Database.EnsureDeleted();
    }

    // ── ToEntity: IssueProviderConfigId mapping ─────────────────────────

    [Fact]
    public async Task AddRunToHistoryAsync_NormalRun_SetsIssueProviderConfigIdNull()
    {
        // ToEntity must set IssueProviderConfigId = null for non-consolidation runs.
        var run = CreateCompletedRun(Guid.NewGuid().ToString(), "owner/repo#1", "Normal run");

        await _sut.AddRunToHistoryAsync(run);

        using var db = new TestPipelineDbContext(_dbOptions);
        var entity = db.PipelineRuns.Single();
        entity.IssueProviderConfigId.Should().BeNull(
            "non-consolidation runs must not carry the consolidation sentinel");
    }

    [Fact]
    public async Task AddRunToHistoryAsync_Upsert_CopiesIssueProviderConfigId()
    {
        // The upsert path (existing entity found) must copy IssueProviderConfigId.
        var runId = Guid.NewGuid();

        // Pre-insert a row (simulating dispatch-time creation with no IssueProviderConfigId yet)
        using (var db = new TestPipelineDbContext(_dbOptions))
        {
            db.PipelineRuns.Add(new PipelineRunEntity
            {
                RunId = runId,
                IssueIdentifier = "owner/repo#5",
                IssueTitle = "",
                FinalStep = PipelineStep.Created,
                StartedAt = DateTimeOffset.UtcNow,
                RunType = PipelineRunType.Implementation,
                IssueProviderConfigId = null
            });
            db.SaveChanges();
        }

        // Complete the run — AddRunToHistoryAsync should upsert including IssueProviderConfigId
        var run = CreateCompletedRun(runId.ToString(), "owner/repo#5", "Updated title");
        await _sut.AddRunToHistoryAsync(run);

        using (var db = new TestPipelineDbContext(_dbOptions))
        {
            var entity = db.PipelineRuns.Single();
            entity.IssueTitle.Should().Be("Updated title");
            entity.IssueProviderConfigId.Should().BeNull(
                "normal run upsert should set IssueProviderConfigId = null");
        }
    }

    // ── DeserializeSummary fallback: InitiatedBy reconstruction ─────────

    [Fact]
    public async Task GetRunHistory_FallbackPath_ReconstructsInitiatedByManual_WhenIssueProviderConfigIdIsNull()
    {
        // Directly pins the fallback: null IssueProviderConfigId → InitiatedBy = "manual".
        // This is the pre-migration row scenario (row has no SummaryJson, no IssueProviderConfigId).
        using (var db = new TestPipelineDbContext(_dbOptions))
        {
            db.PipelineRuns.Add(new PipelineRunEntity
            {
                RunId = Guid.NewGuid(),
                IssueIdentifier = "legacy-manual",
                IssueTitle = "Legacy manual",
                FinalStep = PipelineStep.Completed,
                StartedAt = DateTimeOffset.UtcNow,
                IssueProviderConfigId = null,
                SummaryJson = null
            });
            db.SaveChanges();
        }

        var history = await _sut.GetRunHistoryAsync();

        history.Should().HaveCount(1);
        history[0].InitiatedBy.Should().Be("manual",
            "null IssueProviderConfigId must reconstruct InitiatedBy as 'manual'");
    }

    [Fact]
    public async Task GetRunHistory_FallbackPath_ReconstructsAllFieldsFromColumns()
    {
        // Verifies the DeserializeSummary fallback reconstructs all fields from entity columns.
        var runId = Guid.NewGuid();
        var startedAt = DateTimeOffset.UtcNow.AddHours(-2);
        var completedAt = DateTimeOffset.UtcNow.AddHours(-1);

        using (var db = new TestPipelineDbContext(_dbOptions))
        {
            db.PipelineRuns.Add(new PipelineRunEntity
            {
                RunId = runId,
                IssueIdentifier = "owner/repo#42",
                IssueTitle = "Fallback title",
                FinalStep = PipelineStep.Failed,
                StartedAt = startedAt,
                CompletedAt = completedAt,
                RetryCount = 3,
                PullRequestUrl = "https://github.com/org/repo/pull/99",
                ModelName = "gpt-4o",
                AgentId = "agent-123",
                ProjectName = "MyProject",
                RunType = PipelineRunType.Review,
                IssueProviderConfigId = null,
                SummaryJson = null
            });
            db.SaveChanges();
        }

        var history = await _sut.GetRunHistoryAsync();

        history.Should().HaveCount(1);
        var r = history[0];
        r.RunId.Should().Be(runId.ToString());
        r.IssueIdentifier.Should().Be("owner/repo#42");
        r.IssueTitle.Should().Be("Fallback title");
        r.FinalStep.Should().Be(PipelineStep.Failed);
        r.StartedAtOffset.Should().Be(startedAt);
        r.CompletedAtOffset.Should().Be(completedAt);
        r.RetryCount.Should().Be(3);
        r.PullRequestUrl.Should().Be("https://github.com/org/repo/pull/99");
        r.ModelName.Should().Be("gpt-4o");
        r.AgentId.Should().Be("agent-123");
        r.ProjectName.Should().Be("MyProject");
        r.RunType.Should().Be(PipelineRunType.Review);
        r.InitiatedBy.Should().Be("manual");
    }

    [Fact]
    public async Task GetRunHistory_FallbackPath_ExcludesConsolidationGhost_WhenSummaryJsonIsNull()
    {
        // Consolidation ghost with null SummaryJson must be excluded.
        // IssueProviderConfigId = consolidation sentinel → InitiatedBy reconstructed as "consolidation"
        // → filtered out by the read-time filter.
        using (var db = new TestPipelineDbContext(_dbOptions))
        {
            db.PipelineRuns.Add(new PipelineRunEntity
            {
                RunId = Guid.NewGuid(),
                IssueIdentifier = "consolidation-ghost",
                IssueTitle = "Ghost",
                FinalStep = PipelineStep.Completed,
                StartedAt = DateTimeOffset.UtcNow,
                IssueProviderConfigId = ConsolidationConstants.ProviderConfigId,
                SummaryJson = null
            });
            db.SaveChanges();
        }

        var history = await _sut.GetRunHistoryAsync();

        history.Should().BeEmpty(
            "consolidation ghost entries must be excluded even when SummaryJson is null");
    }

    [Fact]
    public async Task GetRunHistory_FallbackPath_ExcludesConsolidationGhost_WhenSummaryJsonIsCorrupt()
    {
        // Consolidation ghost with corrupt SummaryJson triggers the catch(JsonException) path,
        // then falls back to column reconstruction with IssueProviderConfigId sentinel → excluded.
        using (var db = new TestPipelineDbContext(_dbOptions))
        {
            db.PipelineRuns.Add(new PipelineRunEntity
            {
                RunId = Guid.NewGuid(),
                IssueIdentifier = "consolidation-corrupt",
                IssueTitle = "Corrupt ghost",
                FinalStep = PipelineStep.Completed,
                StartedAt = DateTimeOffset.UtcNow,
                IssueProviderConfigId = ConsolidationConstants.ProviderConfigId,
                SummaryJson = "{ corrupt json"
            });
            db.SaveChanges();
        }

        var history = await _sut.GetRunHistoryAsync();

        history.Should().BeEmpty(
            "consolidation ghost entries must be excluded even when SummaryJson is corrupt");
    }

    [Fact]
    public async Task GetRunHistory_FallbackPath_IncludesNormalRun_WhileExcludingGhost()
    {
        // Mixed scenario: consolidation ghost (IssueProviderConfigId = sentinel, no SummaryJson)
        // and a normal run (null IssueProviderConfigId, no SummaryJson) — only normal run appears.
        using (var db = new TestPipelineDbContext(_dbOptions))
        {
            db.PipelineRuns.Add(new PipelineRunEntity
            {
                RunId = Guid.NewGuid(),
                IssueIdentifier = "consolidation-ghost-2",
                IssueTitle = "Ghost",
                FinalStep = PipelineStep.Completed,
                StartedAt = DateTimeOffset.UtcNow.AddMinutes(-1),
                IssueProviderConfigId = ConsolidationConstants.ProviderConfigId,
                SummaryJson = null
            });
            db.PipelineRuns.Add(new PipelineRunEntity
            {
                RunId = Guid.NewGuid(),
                IssueIdentifier = "normal-fallback",
                IssueTitle = "Normal fallback",
                FinalStep = PipelineStep.Completed,
                StartedAt = DateTimeOffset.UtcNow,
                IssueProviderConfigId = null,
                SummaryJson = null
            });
            db.SaveChanges();
        }

        var history = await _sut.GetRunHistoryAsync();

        history.Should().HaveCount(1);
        history[0].IssueIdentifier.Should().Be("normal-fallback");
        history[0].InitiatedBy.Should().Be("manual");
    }

    [Fact]
    public async Task GetRunHistoryPaged_FallbackPath_ExcludesConsolidationGhost()
    {
        // Paged overload must also exclude consolidation ghosts via fallback path.
        using (var db = new TestPipelineDbContext(_dbOptions))
        {
            db.PipelineRuns.Add(new PipelineRunEntity
            {
                RunId = Guid.NewGuid(),
                IssueIdentifier = "normal-paged",
                IssueTitle = "Normal paged",
                FinalStep = PipelineStep.Completed,
                StartedAt = DateTimeOffset.UtcNow,
                IssueProviderConfigId = null,
                SummaryJson = null
            });
            db.PipelineRuns.Add(new PipelineRunEntity
            {
                RunId = Guid.NewGuid(),
                IssueIdentifier = "ghost-paged",
                IssueTitle = "Ghost paged",
                FinalStep = PipelineStep.Completed,
                StartedAt = DateTimeOffset.UtcNow.AddMinutes(-1),
                IssueProviderConfigId = ConsolidationConstants.ProviderConfigId,
                SummaryJson = null
            });
            db.SaveChanges();
        }

        var result = await _sut.GetRunHistoryAsync(page: 1, pageSize: 10);

        result.Items.Should().HaveCount(1);
        result.Items[0].IssueIdentifier.Should().Be("normal-paged");
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    private static PipelineRun CreateCompletedRun(string runId, string issueIdentifier, string issueTitle)
    {
        var run = PipelineRun.CreateImplementation(new PipelineRunCreationParams
        {
            RunId = runId,
            IssueIdentifier = issueIdentifier,
            IssueTitle = issueTitle,
            IssueProviderConfigId = "ip-1",
            RepoProviderConfigId = "rp-1",
            StartedAt = DateTimeOffset.UtcNow
        });
        run.CurrentStep = PipelineStep.Completed;
        run.MarkCompleted();
        return run;
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
                var indexesToRemove = entityType.GetIndexes()
                    .Where(i => i.GetFilter() != null)
                    .ToList();
                foreach (var index in indexesToRemove)
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
