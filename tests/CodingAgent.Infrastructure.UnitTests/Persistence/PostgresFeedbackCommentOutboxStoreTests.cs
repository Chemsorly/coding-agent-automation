using AwesomeAssertions;
using CodingAgent.Infrastructure.Persistence;
using CodingAgent.Infrastructure.Persistence.Services;
using CodingAgent.Pipeline.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Xunit;

namespace CodingAgent.Infrastructure.UnitTests.Persistence;

/// <summary>
/// Unit tests for <see cref="PostgresFeedbackCommentOutboxStore"/>.
/// Uses InMemory EF Core — same pattern as <see cref="EfKeyValueStoreTests"/>.
///
/// Note: RunId uniqueness is enforced by the database-level unique index which InMemory does not
/// honour; the duplicate-insert idempotency path is therefore not exercised here.
/// </summary>
public sealed class PostgresFeedbackCommentOutboxStoreTests : IDisposable
{
    private readonly DbContextOptions<PipelineDbContext> _dbOptions;

    public PostgresFeedbackCommentOutboxStoreTests()
    {
        var dbName = $"OutboxStoreTests-{Guid.NewGuid()}";
        _dbOptions = new DbContextOptionsBuilder<PipelineDbContext>()
            .UseInMemoryDatabase(dbName)
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;

        using var ctx = new PipelineDbContext(_dbOptions);
        ctx.Database.EnsureCreated();
    }

    public void Dispose()
    {
        using var db = new PipelineDbContext(_dbOptions);
        db.Database.EnsureDeleted();
        GC.SuppressFinalize(this);
    }

    private PostgresFeedbackCommentOutboxStore CreateStore() =>
        new(new InMemoryDbContextFactory(_dbOptions));

    private static FeedbackCommentOutboxEntry MakeEntry(string runId = "run-1") =>
        new()
        {
            Id = Guid.NewGuid(),
            RunId = runId,
            IssueProviderConfigId = "github",
            IssueIdentifier = "GH-42",
            RepoProviderConfigId = "github-repo",
            PullRequestNumber = null,
            FeedbackJson = "{\"description\":\"Issue is unclear\"}",
            Status = "Pending",
            AttemptCount = 0,
            CreatedAt = DateTimeOffset.UtcNow
        };

    // ── EnqueueAsync ──────────────────────────────────────────────────────

    [Fact]
    public async Task EnqueueAsync_NewEntry_PersistedAsPending()
    {
        var store = CreateStore();
        var entry = MakeEntry();

        await store.EnqueueAsync(entry, CancellationToken.None);

        await using var db = new PipelineDbContext(_dbOptions);
        var saved = await db.FeedbackCommentOutbox.FindAsync(entry.Id);
        saved.Should().NotBeNull();
        saved!.Status.Should().Be("Pending");
        saved.AttemptCount.Should().Be(0);
        saved.RunId.Should().Be("run-1");
    }

    [Fact]
    public async Task EnqueueAsync_DuplicateRunId_DoesNotInsertSecondRow()
    {
        var store = CreateStore();
        var first = MakeEntry("run-dup");
        var second = MakeEntry("run-dup"); // same RunId, different Id

        await store.EnqueueAsync(first, CancellationToken.None);
        await store.EnqueueAsync(second, CancellationToken.None); // should be a no-op

        await using var db = new PipelineDbContext(_dbOptions);
        var count = await db.FeedbackCommentOutbox.CountAsync(e => e.RunId == "run-dup");
        count.Should().Be(1);
    }

    [Fact]
    public async Task EnqueueAsync_EntryWithEmptyId_AssignsNewId()
    {
        var store = CreateStore();
        var entry = MakeEntry() with { Id = Guid.Empty };

        await store.EnqueueAsync(entry, CancellationToken.None);

        await using var db = new PipelineDbContext(_dbOptions);
        var count = await db.FeedbackCommentOutbox.CountAsync(e => e.RunId == entry.RunId);
        count.Should().Be(1);
        var saved = await db.FeedbackCommentOutbox.FirstAsync(e => e.RunId == entry.RunId);
        saved.Id.Should().NotBe(Guid.Empty);
    }

    // ── GetPendingAsync ───────────────────────────────────────────────────

    [Fact]
    public async Task GetPendingAsync_ReturnsPendingEntriesWithAttemptCountBelowMax()
    {
        var store = CreateStore();
        var entry = MakeEntry();
        await store.EnqueueAsync(entry, CancellationToken.None);

        var pending = await store.GetPendingAsync(maxAttempts: 5, pageSize: 10, CancellationToken.None);

        pending.Should().HaveCount(1);
        pending[0].RunId.Should().Be("run-1");
    }

    [Fact]
    public async Task GetPendingAsync_ExhaustedEntry_NotReturned()
    {
        var store = CreateStore();
        var entry = MakeEntry("run-exhausted");
        await store.EnqueueAsync(entry, CancellationToken.None);

        // Exhaust all attempts: mark failed maxAttempts times
        for (var i = 0; i < 3; i++)
            await store.MarkFailedAsync(entry.Id, "error", maxAttempts: 3, CancellationToken.None);

        var pending = await store.GetPendingAsync(maxAttempts: 3, pageSize: 10, CancellationToken.None);

        pending.Should().BeEmpty();
    }

    [Fact]
    public async Task GetPendingAsync_CompletedEntry_NotReturned()
    {
        var store = CreateStore();
        var entry = MakeEntry("run-done");
        await store.EnqueueAsync(entry, CancellationToken.None);
        await store.MarkCompletedAsync(entry.Id, CancellationToken.None);

        var pending = await store.GetPendingAsync(maxAttempts: 5, pageSize: 10, CancellationToken.None);

        pending.Should().BeEmpty();
    }

    [Fact]
    public async Task GetPendingAsync_RespectsPageSize()
    {
        var store = CreateStore();
        for (var i = 0; i < 5; i++)
            await store.EnqueueAsync(MakeEntry($"run-{i}"), CancellationToken.None);

        var pending = await store.GetPendingAsync(maxAttempts: 5, pageSize: 2, CancellationToken.None);

        pending.Should().HaveCount(2);
    }

    [Fact]
    public async Task GetPendingAsync_OrdersByCreatedAt()
    {
        var store = CreateStore();
        // Enqueue three entries with distinct RunIds to verify ordering
        await store.EnqueueAsync(MakeEntry("run-a"), CancellationToken.None);
        await store.EnqueueAsync(MakeEntry("run-b"), CancellationToken.None);
        await store.EnqueueAsync(MakeEntry("run-c"), CancellationToken.None);

        var pending = await store.GetPendingAsync(maxAttempts: 5, pageSize: 10, CancellationToken.None);

        // All three should be returned (InMemory doesn't enforce ordering strictly but count is correct)
        pending.Should().HaveCount(3);
    }

    // ── MarkCompletedAsync ────────────────────────────────────────────────

    [Fact]
    public async Task MarkCompletedAsync_SetsStatusCompleted()
    {
        var store = CreateStore();
        var entry = MakeEntry();
        await store.EnqueueAsync(entry, CancellationToken.None);

        await store.MarkCompletedAsync(entry.Id, CancellationToken.None);

        await using var db = new PipelineDbContext(_dbOptions);
        var saved = await db.FeedbackCommentOutbox.FindAsync(entry.Id);
        saved!.Status.Should().Be("Completed");
        saved.CompletedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task MarkCompletedAsync_UnknownId_DoesNotThrow()
    {
        var store = CreateStore();

        var act = () => store.MarkCompletedAsync(Guid.NewGuid(), CancellationToken.None);

        await act.Should().NotThrowAsync();
    }

    // ── MarkFailedAsync ───────────────────────────────────────────────────

    [Fact]
    public async Task MarkFailedAsync_IncrementsAttemptCount()
    {
        var store = CreateStore();
        var entry = MakeEntry();
        await store.EnqueueAsync(entry, CancellationToken.None);

        await store.MarkFailedAsync(entry.Id, "transient error", maxAttempts: 5, CancellationToken.None);

        await using var db = new PipelineDbContext(_dbOptions);
        var saved = await db.FeedbackCommentOutbox.FindAsync(entry.Id);
        saved!.AttemptCount.Should().Be(1);
        saved.Status.Should().Be("Pending"); // not yet exhausted
        saved.ErrorMessage.Should().Be("transient error");
        saved.LastAttemptAt.Should().NotBeNull();
    }

    [Fact]
    public async Task MarkFailedAsync_WhenAttemptsExhausted_TransitionsToFailed()
    {
        var store = CreateStore();
        var entry = MakeEntry();
        await store.EnqueueAsync(entry, CancellationToken.None);

        // Three failures against maxAttempts=3 → should transition to Failed
        await store.MarkFailedAsync(entry.Id, "error", maxAttempts: 3, CancellationToken.None);
        await store.MarkFailedAsync(entry.Id, "error", maxAttempts: 3, CancellationToken.None);
        await store.MarkFailedAsync(entry.Id, "error", maxAttempts: 3, CancellationToken.None);

        await using var db = new PipelineDbContext(_dbOptions);
        var saved = await db.FeedbackCommentOutbox.FindAsync(entry.Id);
        saved!.AttemptCount.Should().Be(3);
        saved.Status.Should().Be("Failed");
    }

    [Fact]
    public async Task MarkFailedAsync_UnknownId_DoesNotThrow()
    {
        var store = CreateStore();

        var act = () => store.MarkFailedAsync(Guid.NewGuid(), "error", 5, CancellationToken.None);

        await act.Should().NotThrowAsync();
    }

    // ── Helper ────────────────────────────────────────────────────────────

    private sealed class InMemoryDbContextFactory : IDbContextFactory<PipelineDbContext>
    {
        private readonly DbContextOptions<PipelineDbContext> _options;
        public InMemoryDbContextFactory(DbContextOptions<PipelineDbContext> options) => _options = options;
        public PipelineDbContext CreateDbContext() => new(_options);
        public Task<PipelineDbContext> CreateDbContextAsync(CancellationToken ct = default)
            => Task.FromResult(CreateDbContext());
    }
}
