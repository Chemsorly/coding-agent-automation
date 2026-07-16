using AwesomeAssertions;
using CodingAgentWebUI.Infrastructure.Persistence;
using CodingAgentWebUI.Infrastructure.Persistence.Services;
using CodingAgentWebUI.Pipeline.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Moq;
using ILogger = Serilog.ILogger;

namespace CodingAgentWebUI.Pipeline.UnitTests.Services;

/// <summary>
/// Tests for async overloads in <see cref="PostgresPipelineRunHistoryService"/>.
/// Validates Issue #10 fix: async overloads complete without deadlock.
/// </summary>
public sealed class PostgresPipelineRunHistoryServiceAsyncTests : IDisposable
{
    private readonly DbContextOptions<PipelineDbContext> _dbOptions;
    private readonly InMemoryDbContextFactory _dbFactory;
    private readonly PostgresPipelineRunHistoryService _sut;

    public PostgresPipelineRunHistoryServiceAsyncTests()
    {
        _dbOptions = new DbContextOptionsBuilder<PipelineDbContext>()
            .UseInMemoryDatabase(databaseName: $"AsyncRunHistoryTests-{Guid.NewGuid()}")
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;

        using (var ctx = new InMemoryPipelineDbContext(_dbOptions))
        {
            ctx.Database.EnsureCreated();
        }

        _dbFactory = new InMemoryDbContextFactory(_dbOptions);
        _sut = new PostgresPipelineRunHistoryService(_dbFactory, new Mock<ILogger>().Object);
    }

    public void Dispose()
    {
        using var db = new InMemoryPipelineDbContext(_dbOptions);
        db.Database.EnsureDeleted();
    }

    [Fact]
    public async Task AddRunToHistoryAsync_PersistsWithoutDeadlock()
    {
        // Arrange
        var runId = Guid.NewGuid().ToString();
        var run = CreateCompletedRun(runId, "owner/repo#1", "Async test run");

        // Act — the test itself proves no deadlock by completing within xunit timeout
        await _sut.AddRunToHistoryAsync(run);

        // Assert
        using var db = new InMemoryPipelineDbContext(_dbOptions);
        var entities = db.PipelineRuns.ToList();
        entities.Should().HaveCount(1);
        entities[0].IssueIdentifier.Should().Be("owner/repo#1");
        entities[0].IssueTitle.Should().Be("Async test run");
    }

    [Fact]
    public async Task GetRunHistoryAsync_ReturnsPersistedRuns()
    {
        // Arrange
        var run1 = CreateCompletedRun(Guid.NewGuid().ToString(), "issue-1", "First");
        var run2 = CreateCompletedRun(Guid.NewGuid().ToString(), "issue-2", "Second");
        await _sut.AddRunToHistoryAsync(run1);
        await _sut.AddRunToHistoryAsync(run2);

        // Act
        var history = await _sut.GetRunHistoryAsync();

        // Assert
        history.Should().HaveCount(2);
    }

    [Fact]
    public async Task AddRunToHistoryAsync_WithCancellation_DoesNotThrowWhenNotCancelled()
    {
        var run = CreateCompletedRun(Guid.NewGuid().ToString(), "issue-ct", "Cancellation test");
        var cts = new CancellationTokenSource();

        // Should complete normally when token is not cancelled
        await _sut.AddRunToHistoryAsync(run, cts.Token);

        var history = await _sut.GetRunHistoryAsync(cts.Token);
        history.Should().HaveCount(1);
    }

    // ── CancellationToken propagation tests ─────────────────────────────

    [Fact]
    public async Task GetRunHistoryAsync_CancelledToken_ThrowsOperationCanceled()
    {
        // Arrange: pre-cancel the token
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        // Act & Assert: the cancellation token is propagated to the DB layer,
        // causing OperationCanceledException (or TaskCanceledException) to be thrown.
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => _sut.GetRunHistoryAsync(cts.Token));
    }

    [Fact]
    public async Task AddRunToHistoryAsync_CancelledToken_PropagatesTokenToDbFactory()
    {
        // AddRunToHistoryAsync catches exceptions (by design — persistence failures are non-fatal).
        // We verify token propagation by tracking what token the factory received.
        var trackingFactory = new CancellationTrackingDbContextFactory(_dbOptions);
        var sut = new PostgresPipelineRunHistoryService(trackingFactory, new Mock<ILogger>().Object);

        var run = CreateCompletedRun(Guid.NewGuid().ToString(), "issue-cancel", "Cancel test");
        using var cts = new CancellationTokenSource();

        // Act
        await sut.AddRunToHistoryAsync(run, cts.Token);

        // Assert: the CancellationToken was forwarded to CreateDbContextAsync
        trackingFactory.LastCancellationToken.Should().Be(cts.Token);
    }

    [Fact]
    public async Task GetRunHistoryAsync_TokenPassedToCreateDbContextAsync()
    {
        // Arrange: use a factory that tracks whether the token was forwarded
        var trackingFactory = new CancellationTrackingDbContextFactory(_dbOptions);
        var sut = new PostgresPipelineRunHistoryService(trackingFactory, new Mock<ILogger>().Object);

        using var cts = new CancellationTokenSource();

        // Act
        await sut.GetRunHistoryAsync(cts.Token);

        // Assert: the CancellationToken was forwarded to CreateDbContextAsync
        trackingFactory.LastCancellationToken.Should().Be(cts.Token);
    }

    [Fact]
    public async Task AddRunToHistoryAsync_TokenPassedToCreateDbContextAsync()
    {
        // Arrange: use a factory that tracks whether the token was forwarded
        var trackingFactory = new CancellationTrackingDbContextFactory(_dbOptions);
        var sut = new PostgresPipelineRunHistoryService(trackingFactory, new Mock<ILogger>().Object);

        var run = CreateCompletedRun(Guid.NewGuid().ToString(), "issue-track", "Track CT");
        using var cts = new CancellationTokenSource();

        // Act
        await sut.AddRunToHistoryAsync(run, cts.Token);

        // Assert: the CancellationToken was forwarded to CreateDbContextAsync
        trackingFactory.LastCancellationToken.Should().Be(cts.Token);
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    private static PipelineRun CreateCompletedRun(string runId, string issueIdentifier, string issueTitle)
    {
        var run = PipelineRun.Create(
            runId, issueIdentifier, issueTitle,
            "ip-1", "rp-1",
            startedAt: DateTimeOffset.UtcNow);
        run.CurrentStep = PipelineStep.Completed;
        run.MarkCompleted();
        return run;
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
                    entityType.RemoveIndex(index);
            }
        }
    }

    private sealed class InMemoryDbContextFactory : IDbContextFactory<PipelineDbContext>
    {
        private readonly DbContextOptions<PipelineDbContext> _options;
        public InMemoryDbContextFactory(DbContextOptions<PipelineDbContext> options) => _options = options;
        public PipelineDbContext CreateDbContext() => new InMemoryPipelineDbContext(_options);
        public Task<PipelineDbContext> CreateDbContextAsync(CancellationToken ct = default) => Task.FromResult(CreateDbContext());
    }

    /// <summary>
    /// A factory that records whether CancellationToken was forwarded to CreateDbContextAsync.
    /// Used to verify CancellationToken propagation without relying on cancellation-throwing behavior
    /// (which may not be supported by the in-memory provider).
    /// </summary>
    private sealed class CancellationTrackingDbContextFactory : IDbContextFactory<PipelineDbContext>
    {
        private readonly DbContextOptions<PipelineDbContext> _options;

        public CancellationToken LastCancellationToken { get; private set; }

        public CancellationTrackingDbContextFactory(DbContextOptions<PipelineDbContext> options) => _options = options;

        public PipelineDbContext CreateDbContext() => new InMemoryPipelineDbContext(_options);

        public Task<PipelineDbContext> CreateDbContextAsync(CancellationToken ct = default)
        {
            LastCancellationToken = ct;
            return Task.FromResult(CreateDbContext());
        }
    }
}
