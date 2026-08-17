using AwesomeAssertions;
using CodingAgentWebUI.Infrastructure.Persistence;
using CodingAgentWebUI.Infrastructure.Persistence.Services;
using CodingAgentWebUI.Pipeline.Interfaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace CodingAgentWebUI.Infrastructure.UnitTests.Persistence;

/// <summary>
/// Unit tests for <see cref="EfKeyValueStore"/>.
/// Uses InMemory EF Core — same pattern as <see cref="PostgresLoopStateStoreContractTests"/>.
/// </summary>
public class EfKeyValueStoreTests : IDisposable
{
    private readonly DbContextOptions<PipelineDbContext> _dbOptions;

    public EfKeyValueStoreTests()
    {
        var dbName = $"EfKeyValueStoreTests-{Guid.NewGuid()}";
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

    private IKeyValueStore CreateStore()
        => new EfKeyValueStore(new EfKeyValueStoreTestDbContextFactory(_dbOptions));

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
}

/// <summary>Helper: IDbContextFactory backed by InMemory provider.</summary>
file class EfKeyValueStoreTestDbContextFactory : IDbContextFactory<PipelineDbContext>
{
    private readonly DbContextOptions<PipelineDbContext> _options;
    public EfKeyValueStoreTestDbContextFactory(DbContextOptions<PipelineDbContext> options) => _options = options;
    public PipelineDbContext CreateDbContext() => new(_options);
    public Task<PipelineDbContext> CreateDbContextAsync(CancellationToken ct = default)
        => Task.FromResult(CreateDbContext());
}
