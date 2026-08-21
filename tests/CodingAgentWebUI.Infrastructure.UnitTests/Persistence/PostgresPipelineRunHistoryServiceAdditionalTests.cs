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
/// Additional unit tests for <see cref="PostgresPipelineRunHistoryService"/>:
/// feedbackOnly=false passthrough, page validation, RunType fallback reconstruction,
/// and HasMore pagination behaviour.
/// Uses the same InMemory EF pattern as <see cref="PostgresPipelineRunHistoryServiceGhostFilteringTests"/>.
/// </summary>
public sealed class PostgresPipelineRunHistoryServiceAdditionalTests : IDisposable
{
    private readonly DbContextOptions<PipelineDbContext> _dbOptions;
    private readonly PostgresPipelineRunHistoryService _sut;

    public PostgresPipelineRunHistoryServiceAdditionalTests()
    {
        _dbOptions = new DbContextOptionsBuilder<PipelineDbContext>()
            .UseInMemoryDatabase(databaseName: $"AdditionalTests-{Guid.NewGuid()}")
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

    // ── feedbackOnly=false passthrough ────────────────────────────────────────

    [Fact]
    public async Task GetRunHistoryAsync_WithFeedbackOnly_False_ReturnsNormalItems()
    {
        // feedbackOnly=false must return all non-consolidation items via the normal paged path.
        using (var db = new TestPipelineDbContext(_dbOptions))
        {
            db.PipelineRuns.Add(CreateRunEntity(Guid.NewGuid(), "owner/repo#1", "Run one"));
            db.PipelineRuns.Add(CreateRunEntity(Guid.NewGuid(), "owner/repo#2", "Run two"));
            db.SaveChanges();
        }

        var result = await _sut.GetRunHistoryAsync(page: 1, pageSize: 10, feedbackOnly: false);

        result.Items.Should().HaveCount(2);
    }

    [Fact]
    public async Task GetRunHistoryAsync_WithFeedbackOnly_InvalidPage_ThrowsArgumentOutOfRangeException()
    {
        // page=0 is below the minimum of 1 and must throw immediately.
        var act = async () => await _sut.GetRunHistoryAsync(page: 0, pageSize: 10, feedbackOnly: false);

        await act.Should().ThrowAsync<ArgumentOutOfRangeException>();
    }

    [Fact]
    public async Task GetRunHistoryAsync_WithFeedbackOnly_EmptyTable_FeedbackOnlyTrue_ThrowsInvalidOperationException()
    {
        // The feedbackOnly=true path uses FromSqlRaw (Postgres JSONB ? operator), which is
        // not supported by the InMemory EF provider. This test documents the known constraint:
        // feedbackOnly queries require a real Postgres provider and will throw in unit-test context.
        var act = async () => await _sut.GetRunHistoryAsync(page: 1, pageSize: 10, feedbackOnly: true);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*wasn't handled by provider code*");
    }

    // ── DeserializeSummary fallback: RunType preservation ────────────────────

    [Fact]
    public async Task DeserializeSummary_FallbackPath_PreservesImplementationRunType()
    {
        using (var db = new TestPipelineDbContext(_dbOptions))
        {
            db.PipelineRuns.Add(CreateRunEntityWithNullSummary(Guid.NewGuid(), "repo#10", PipelineRunType.Implementation));
            db.SaveChanges();
        }

        var history = await _sut.GetRunHistoryAsync();

        history.Should().HaveCount(1);
        history[0].RunType.Should().Be(PipelineRunType.Implementation,
            "RunType must be reconstructed from the entity column when SummaryJson is null");
    }

    [Fact]
    public async Task DeserializeSummary_FallbackPath_PreservesReviewRunType()
    {
        using (var db = new TestPipelineDbContext(_dbOptions))
        {
            db.PipelineRuns.Add(CreateRunEntityWithNullSummary(Guid.NewGuid(), "repo#11", PipelineRunType.Review));
            db.SaveChanges();
        }

        var history = await _sut.GetRunHistoryAsync();

        history.Should().HaveCount(1);
        history[0].RunType.Should().Be(PipelineRunType.Review);
    }

    [Fact]
    public async Task DeserializeSummary_FallbackPath_PreservesDecompositionRunType()
    {
        using (var db = new TestPipelineDbContext(_dbOptions))
        {
            db.PipelineRuns.Add(CreateRunEntityWithNullSummary(Guid.NewGuid(), "repo#12", PipelineRunType.Decomposition));
            db.SaveChanges();
        }

        var history = await _sut.GetRunHistoryAsync();

        history.Should().HaveCount(1);
        history[0].RunType.Should().Be(PipelineRunType.Decomposition);
    }

    // ── HasMore pagination ────────────────────────────────────────────────────

    [Fact]
    public async Task GetRunHistoryAsync_PageSize1_HasMore_True()
    {
        // 3 items, pageSize=1 → HasMore must be true
        using (var db = new TestPipelineDbContext(_dbOptions))
        {
            db.PipelineRuns.Add(CreateRunEntity(Guid.NewGuid(), "owner/repo#20", "Run A"));
            db.PipelineRuns.Add(CreateRunEntity(Guid.NewGuid(), "owner/repo#21", "Run B"));
            db.PipelineRuns.Add(CreateRunEntity(Guid.NewGuid(), "owner/repo#22", "Run C"));
            db.SaveChanges();
        }

        var result = await _sut.GetRunHistoryAsync(page: 1, pageSize: 1, feedbackOnly: false);

        result.Items.Should().HaveCount(1);
        result.HasMore.Should().BeTrue("there are more items beyond the first page");
    }

    [Fact]
    public async Task GetRunHistoryAsync_PageSize10_HasMore_False()
    {
        // 2 items, pageSize=10 → HasMore must be false
        using (var db = new TestPipelineDbContext(_dbOptions))
        {
            db.PipelineRuns.Add(CreateRunEntity(Guid.NewGuid(), "owner/repo#30", "Run X"));
            db.PipelineRuns.Add(CreateRunEntity(Guid.NewGuid(), "owner/repo#31", "Run Y"));
            db.SaveChanges();
        }

        var result = await _sut.GetRunHistoryAsync(page: 1, pageSize: 10, feedbackOnly: false);

        result.Items.Should().HaveCount(2);
        result.HasMore.Should().BeFalse("all items fit on the first page");
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static PipelineRunEntity CreateRunEntity(Guid runId, string issueIdentifier, string title) =>
        new()
        {
            RunId = runId,
            IssueIdentifier = issueIdentifier,
            IssueTitle = title,
            FinalStep = PipelineStep.Completed,
            StartedAt = DateTimeOffset.UtcNow,
            RunType = PipelineRunType.Implementation,
            IssueProviderConfigId = null,
            SummaryJson = null
        };

    private static PipelineRunEntity CreateRunEntityWithNullSummary(
        Guid runId,
        string issueIdentifier,
        PipelineRunType runType) =>
        new()
        {
            RunId = runId,
            IssueIdentifier = issueIdentifier,
            IssueTitle = "Fallback title",
            FinalStep = PipelineStep.Completed,
            StartedAt = DateTimeOffset.UtcNow,
            RunType = runType,
            IssueProviderConfigId = null,
            SummaryJson = null
        };

    // ── Test Infrastructure (copied from GhostFilteringTests) ────────────────

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
