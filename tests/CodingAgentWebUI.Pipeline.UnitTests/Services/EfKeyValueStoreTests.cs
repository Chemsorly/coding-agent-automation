using AwesomeAssertions;
using CodingAgentWebUI.Infrastructure.Persistence;
using CodingAgentWebUI.Infrastructure.Persistence.Services;
using CodingAgentWebUI.Pipeline.Interfaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace CodingAgentWebUI.Pipeline.UnitTests.Services;

/// <summary>
/// Tests for EfKeyValueStore using in-memory EF Core.
/// Covers: Get (found / not-found), Set (insert / update), Delete (found / not-found).
/// </summary>
public sealed class EfKeyValueStoreTests : IDisposable
{
    private readonly DbContextOptions<PipelineDbContext> _dbOptions;
    private readonly TestDbContextFactory _factory;
    private readonly EfKeyValueStore _sut;

    public EfKeyValueStoreTests()
    {
        _dbOptions = new DbContextOptionsBuilder<PipelineDbContext>()
            .UseInMemoryDatabase(databaseName: $"EfKeyValueStore-{Guid.NewGuid()}")
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;

        using var ctx = new TestPipelineDbContext(_dbOptions);
        ctx.Database.EnsureCreated();

        _factory = new TestDbContextFactory(_dbOptions);
        _sut = new EfKeyValueStore(_factory);
    }

    public void Dispose()
    {
        using var ctx = new TestPipelineDbContext(_dbOptions);
        ctx.Database.EnsureDeleted();
    }

    // ── GetAsync ──────────────────────────────────────────────────────────

    [Fact]
    public async Task GetAsync_WhenKeyNotFound_ReturnsNull()
    {
        var result = await _sut.GetAsync("missing-key", CancellationToken.None);
        result.Should().BeNull();
    }

    [Fact]
    public async Task GetAsync_WhenKeyExists_ReturnsValue()
    {
        await _sut.SetAsync("my-key", "my-value", CancellationToken.None);

        var result = await _sut.GetAsync("my-key", CancellationToken.None);
        result.Should().Be("my-value");
    }

    // ── SetAsync ──────────────────────────────────────────────────────────

    [Fact]
    public async Task SetAsync_Insert_Persists()
    {
        await _sut.SetAsync("k1", "v1", CancellationToken.None);

        var result = await _sut.GetAsync("k1", CancellationToken.None);
        result.Should().Be("v1");
    }

    [Fact]
    public async Task SetAsync_Update_OverwritesExistingValue()
    {
        await _sut.SetAsync("k1", "original", CancellationToken.None);
        await _sut.SetAsync("k1", "updated", CancellationToken.None);

        var result = await _sut.GetAsync("k1", CancellationToken.None);
        result.Should().Be("updated");
    }

    [Fact]
    public async Task SetAsync_MultipleKeys_StoredIndependently()
    {
        await _sut.SetAsync("key-a", "value-a", CancellationToken.None);
        await _sut.SetAsync("key-b", "value-b", CancellationToken.None);

        (await _sut.GetAsync("key-a", CancellationToken.None)).Should().Be("value-a");
        (await _sut.GetAsync("key-b", CancellationToken.None)).Should().Be("value-b");
    }

    // ── DeleteAsync ───────────────────────────────────────────────────────

    [Fact]
    public async Task DeleteAsync_WhenKeyExists_Removes()
    {
        await _sut.SetAsync("del-key", "val", CancellationToken.None);
        await _sut.DeleteAsync("del-key", CancellationToken.None);

        var result = await _sut.GetAsync("del-key", CancellationToken.None);
        result.Should().BeNull();
    }

    [Fact]
    public async Task DeleteAsync_WhenKeyNotFound_DoesNotThrow()
    {
        var act = () => _sut.DeleteAsync("non-existent", CancellationToken.None);
        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task DeleteAsync_OnlyRemovesTargetKey()
    {
        await _sut.SetAsync("keep", "value", CancellationToken.None);
        await _sut.SetAsync("remove", "value", CancellationToken.None);

        await _sut.DeleteAsync("remove", CancellationToken.None);

        (await _sut.GetAsync("keep", CancellationToken.None)).Should().Be("value");
        (await _sut.GetAsync("remove", CancellationToken.None)).Should().BeNull();
    }

    // ── Inner helpers (same pattern as AgentHubFacadeProgressTrackingTests) ──

    private sealed class TestPipelineDbContext(DbContextOptions<PipelineDbContext> options)
        : PipelineDbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);
            // Disable row-version concurrency tokens — incompatible with in-memory DB
            foreach (var entity in modelBuilder.Model.GetEntityTypes())
            {
                foreach (var prop in entity.GetProperties()
                    .Where(p => p.IsConcurrencyToken && p.ClrType == typeof(byte[])))
                {
                    prop.IsConcurrencyToken = false;
                }
            }
        }
    }

    private sealed class TestDbContextFactory(DbContextOptions<PipelineDbContext> options)
        : IDbContextFactory<PipelineDbContext>
    {
        public PipelineDbContext CreateDbContext() => new TestPipelineDbContext(options);
    }
}
