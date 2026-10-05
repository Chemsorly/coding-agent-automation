using AwesomeAssertions;
using CodingAgent.Infrastructure.Persistence;
using CodingAgent.Infrastructure.Persistence.Services;
using CodingAgent.Pipeline.Interfaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace CodingAgent.Infrastructure.UnitTests.Persistence;

/// <summary>
/// Unit tests for <see cref="PostgresKeyValueStore"/>.
/// Uses InMemory EF Core — same pattern as <see cref="PostgresLoopStateStoreContractTests"/>.
/// </summary>
public class PostgresKeyValueStoreTests : IDisposable
{
    private readonly DbContextOptions<PipelineDbContext> _dbOptions;

    public PostgresKeyValueStoreTests()
    {
        var dbName = $"PostgresKeyValueStoreTests-{Guid.NewGuid()}";
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

    private PostgresKeyValueStore CreateStore()
        => new PostgresKeyValueStore(new PostgresKeyValueStoreTestDbContextFactory(_dbOptions));

    // ── GetAsync ─────────────────────────────────────────────────────────

    [Fact]
    public async Task GetAsync_AbsentKey_ReturnsNull()
    {
        var store = CreateStore();

        var result = await store.GetAsync("missing-key", CancellationToken.None);

        result.Should().BeNull();
    }

    // ── SetAsync + GetAsync roundtrip ─────────────────────────────────────

    [Fact]
    public async Task SetAsync_ThenGetAsync_ReturnsValue()
    {
        var store = CreateStore();

        await store.SetAsync("my-key", "my-value", CancellationToken.None);
        var result = await store.GetAsync("my-key", CancellationToken.None);

        result.Should().Be("my-value");
    }

    [Fact]
    public async Task SetAsync_ExistingKey_UpdatesWithoutDuplication()
    {
        var store = CreateStore();
        await store.SetAsync("dup-key", "first-value", CancellationToken.None);

        await store.SetAsync("dup-key", "second-value", CancellationToken.None);

        var result = await store.GetAsync("dup-key", CancellationToken.None);
        result.Should().Be("second-value");

        // Confirm only one row exists for the key
        await using var db = new PipelineDbContext(_dbOptions);
        var count = await db.KeyValueStore.CountAsync(kv => kv.Key == "dup-key");
        count.Should().Be(1);
    }

    [Fact]
    public async Task SetAsync_MultipleKeys_StoredIndependently()
    {
        var store = CreateStore();

        await store.SetAsync("key-a", "value-a", CancellationToken.None);
        await store.SetAsync("key-b", "value-b", CancellationToken.None);

        (await store.GetAsync("key-a", CancellationToken.None)).Should().Be("value-a");
        (await store.GetAsync("key-b", CancellationToken.None)).Should().Be("value-b");
    }

    // ── DeleteAsync ──────────────────────────────────────────────────────

    [Fact]
    public async Task DeleteAsync_AfterSet_GetAsyncReturnsNull()
    {
        var store = CreateStore();
        await store.SetAsync("del-key", "value", CancellationToken.None);

        await store.DeleteAsync("del-key", CancellationToken.None);

        var result = await store.GetAsync("del-key", CancellationToken.None);
        result.Should().BeNull();
    }

    [Fact]
    public async Task DeleteAsync_WhenKeyNotFound_DoesNotThrow()
    {
        var store = CreateStore();

        var act = () => store.DeleteAsync("non-existent", CancellationToken.None);
        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task DeleteAsync_OnlyRemovesTargetKey()
    {
        var store = CreateStore();

        await store.SetAsync("keep", "value", CancellationToken.None);
        await store.SetAsync("remove", "value", CancellationToken.None);

        await store.DeleteAsync("remove", CancellationToken.None);

        (await store.GetAsync("keep", CancellationToken.None)).Should().Be("value");
        (await store.GetAsync("remove", CancellationToken.None)).Should().BeNull();
    }
}

/// <summary>Helper: IDbContextFactory backed by InMemory provider.</summary>
file class PostgresKeyValueStoreTestDbContextFactory : IDbContextFactory<PipelineDbContext>
{
    private readonly DbContextOptions<PipelineDbContext> _options;
    public PostgresKeyValueStoreTestDbContextFactory(DbContextOptions<PipelineDbContext> options) => _options = options;
    public PipelineDbContext CreateDbContext() => new(_options);
    public Task<PipelineDbContext> CreateDbContextAsync(CancellationToken ct = default)
        => Task.FromResult(CreateDbContext());
}
