using AwesomeAssertions;
using CodingAgentWebUI.Infrastructure.Persistence;
using CodingAgentWebUI.Infrastructure.Persistence.Services;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace CodingAgentWebUI.Infrastructure.UnitTests.Persistence;

/// <summary>
/// Contract tests for <see cref="ILoopStateStore"/> implementations.
/// Both FileSystem-backed and Postgres-backed stores must satisfy these behavioral contracts.
/// Prevents behavioral drift between legacy (filesystem) and DB (Postgres) modes.
///
/// Pattern follows <see cref="ConfigurationStoreContractTests"/>.
/// </summary>
public abstract class LoopStateStoreContractTests : IDisposable
{
    protected abstract ILoopStateStore CreateStore();
    public virtual void Dispose() { }

    // ── ReadAsync ────────────────────────────────────────────────────────

    [Fact]
    public async Task Read_EmptyStore_ReturnsNull()
    {
        var store = CreateStore();

        var state = await store.ReadAsync(CancellationToken.None);

        state.Should().BeNull();
    }

    // ── WriteAsync + ReadAsync roundtrip ─────────────────────────────────

    [Fact]
    public async Task Write_ThenRead_ReturnsPersistedState()
    {
        var store = CreateStore();
        var state = new LoopState
        {
            IsActive = true,
            StartedAt = new DateTimeOffset(2026, 7, 1, 10, 0, 0, TimeSpan.Zero),
            StoppedAt = null
        };

        await store.WriteAsync(state, CancellationToken.None);
        var loaded = await store.ReadAsync(CancellationToken.None);

        loaded.Should().NotBeNull();
        loaded!.IsActive.Should().BeTrue();
        loaded.StartedAt.Should().Be(state.StartedAt);
        loaded.StoppedAt.Should().BeNull();
    }

    [Fact]
    public async Task Write_Overwrites_PreviousState()
    {
        var store = CreateStore();

        await store.WriteAsync(new LoopState { IsActive = true, StartedAt = DateTimeOffset.UtcNow }, CancellationToken.None);
        await store.WriteAsync(new LoopState { IsActive = false, StoppedAt = DateTimeOffset.UtcNow }, CancellationToken.None);

        var loaded = await store.ReadAsync(CancellationToken.None);
        loaded.Should().NotBeNull();
        loaded!.IsActive.Should().BeFalse();
        loaded.StoppedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task Write_PreservesAllProperties()
    {
        var store = CreateStore();
        var startedAt = new DateTimeOffset(2026, 6, 15, 8, 30, 0, TimeSpan.Zero);
        var stoppedAt = new DateTimeOffset(2026, 6, 15, 9, 45, 0, TimeSpan.Zero);

        var state = new LoopState
        {
            IsActive = false,
            StartedAt = startedAt,
            StoppedAt = stoppedAt
        };

        await store.WriteAsync(state, CancellationToken.None);
        var loaded = await store.ReadAsync(CancellationToken.None);

        loaded.Should().NotBeNull();
        loaded!.IsActive.Should().BeFalse();
        loaded.StartedAt.Should().Be(startedAt);
        loaded.StoppedAt.Should().Be(stoppedAt);
    }

    // ── DeleteAsync ─────────────────────────────────────────────────────

    [Fact]
    public async Task Delete_AfterWrite_ReadReturnsNull()
    {
        var store = CreateStore();
        await store.WriteAsync(new LoopState { IsActive = true }, CancellationToken.None);

        await store.DeleteAsync(CancellationToken.None);

        var loaded = await store.ReadAsync(CancellationToken.None);
        loaded.Should().BeNull();
    }

    [Fact]
    public async Task Delete_EmptyStore_DoesNotThrow()
    {
        var store = CreateStore();

        var act = () => store.DeleteAsync(CancellationToken.None);

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task Delete_ThenWrite_NewStateReadable()
    {
        var store = CreateStore();
        await store.WriteAsync(new LoopState { IsActive = true, StartedAt = DateTimeOffset.UtcNow }, CancellationToken.None);
        await store.DeleteAsync(CancellationToken.None);

        var newState = new LoopState { IsActive = false, StoppedAt = DateTimeOffset.UtcNow };
        await store.WriteAsync(newState, CancellationToken.None);

        var loaded = await store.ReadAsync(CancellationToken.None);
        loaded.Should().NotBeNull();
        loaded!.IsActive.Should().BeFalse();
    }
}

// ── FileSystem-backed implementation ────────────────────────────────────────

/// <summary>
/// Runs the contract tests against <see cref="FileSystemLoopStateStore"/>.
/// </summary>
public class FileSystemLoopStateStoreContractTests : LoopStateStoreContractTests
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"contract-loop-fs-{Guid.NewGuid()}");

    public FileSystemLoopStateStoreContractTests()
    {
        Directory.CreateDirectory(_tempDir);
    }

    protected override ILoopStateStore CreateStore()
        => new FileSystemLoopStateStore(Path.Combine(_tempDir, "loop-state.json"));

    public override void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }
}

// ── Postgres-backed implementation (InMemory EF) ────────────────────────────

/// <summary>
/// Runs the contract tests against <see cref="PostgresLoopStateStore"/> using InMemory EF Core.
/// </summary>
public class PostgresLoopStateStoreContractTests : LoopStateStoreContractTests
{
    private readonly DbContextOptions<PipelineDbContext> _dbOptions;

    public PostgresLoopStateStoreContractTests()
    {
        var dbName = $"LoopStateContractTests-{Guid.NewGuid()}";
        _dbOptions = new DbContextOptionsBuilder<PipelineDbContext>()
            .UseInMemoryDatabase(dbName)
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;

        using var ctx = new PipelineDbContext(_dbOptions);
        ctx.Database.EnsureCreated();
    }

    protected override ILoopStateStore CreateStore()
    {
        var factory = new LoopStateContractTestDbContextFactory(_dbOptions);
        return new PostgresLoopStateStore(factory);
    }

    public override void Dispose()
    {
        using var db = new PipelineDbContext(_dbOptions);
        db.Database.EnsureDeleted();
    }
}

/// <summary>Helper: IDbContextFactory for InMemory provider.</summary>
file class LoopStateContractTestDbContextFactory : IDbContextFactory<PipelineDbContext>
{
    private readonly DbContextOptions<PipelineDbContext> _options;
    public LoopStateContractTestDbContextFactory(DbContextOptions<PipelineDbContext> options) => _options = options;
    public PipelineDbContext CreateDbContext() => new(_options);
    public Task<PipelineDbContext> CreateDbContextAsync(CancellationToken ct = default)
        => Task.FromResult(CreateDbContext());
}
