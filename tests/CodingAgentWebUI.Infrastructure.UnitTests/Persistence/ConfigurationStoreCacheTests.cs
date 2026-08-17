using AwesomeAssertions;
using CodingAgentWebUI.Infrastructure.Persistence;
using CodingAgentWebUI.Infrastructure.Persistence.Entities;
using CodingAgentWebUI.Infrastructure.Persistence.Stores;
using CodingAgentWebUI.Pipeline;
using CodingAgentWebUI.Pipeline.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using System.Text.Json;

namespace CodingAgentWebUI.Infrastructure.UnitTests.Persistence;

/// <summary>
/// Cache-invalidation tests for <see cref="PostgresConfigurationStore"/>.
/// Uses InMemory EF Core with TTL=1ms so cache entries expire immediately,
/// letting tests verify that writes invalidate the cache rather than serve stale data.
/// </summary>
public sealed class ConfigurationStoreCacheTests : IDisposable
{
    private readonly DbContextOptions<PipelineDbContext> _dbOptions;

    public ConfigurationStoreCacheTests()
    {
        var dbName = $"CacheTests-{Guid.NewGuid()}";
        _dbOptions = new DbContextOptionsBuilder<PipelineDbContext>()
            .UseInMemoryDatabase(dbName)
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        using var ctx = new CacheTestPipelineDbContext(_dbOptions);
        ctx.Database.EnsureCreated();
    }

    public void Dispose()
    {
        using var db = new CacheTestPipelineDbContext(_dbOptions);
        db.Database.EnsureDeleted();
    }

    private PostgresConfigurationStore CreateStore(TimeSpan? ttl = null) =>
        new PostgresConfigurationStore(
            new CacheTestDbContextFactory(_dbOptions),
            cacheTtl: ttl ?? TimeSpan.FromMilliseconds(1));

    // ── _pipelineConfigCache (permanent until write) ──────────────────────

    [Fact]
    public async Task PipelineConfig_FirstLoad_PopulatesCache_SecondCallReturnsSameObject()
    {
        // Arrange
        var store = CreateStore();
        var saved = new PipelineConfiguration { MaxRetries = 3, WorkspaceBaseDirectory = "/cached" };
        await store.SavePipelineConfigAsync(saved, CancellationToken.None);

        // Act
        var first = await store.LoadPipelineConfigAsync(CancellationToken.None);
        var second = await store.LoadPipelineConfigAsync(CancellationToken.None);

        // Assert: same object reference proves the second call returned the cached instance
        second.Should().BeSameAs(first);
    }

    [Fact]
    public async Task SavePipelineConfig_ReplacesCache_SubsequentLoadReturnsSavedValue()
    {
        // Arrange: load to prime the cache with the initial value
        var store = CreateStore();
        await store.SavePipelineConfigAsync(
            new PipelineConfiguration { MaxRetries = 1, WorkspaceBaseDirectory = "/original" },
            CancellationToken.None);
        var _ = await store.LoadPipelineConfigAsync(CancellationToken.None);

        // Act: save a new value through the store
        await store.SavePipelineConfigAsync(
            new PipelineConfiguration { MaxRetries = 42, WorkspaceBaseDirectory = "/after-save" },
            CancellationToken.None);

        // The next load must return the newly saved value, not the previously cached "/original"
        var loaded = await store.LoadPipelineConfigAsync(CancellationToken.None);

        // Assert: cache was replaced with the saved value (not stale)
        loaded.WorkspaceBaseDirectory.Should().Be("/after-save");
        loaded.MaxRetries.Should().Be(42);
    }

    [Fact]
    public async Task UpdatePipelineConfig_ReplacesCache_SubsequentLoadReturnsUpdatedValue()
    {
        // Arrange: prime the cache with an initial value
        var store = CreateStore();
        await store.SavePipelineConfigAsync(
            new PipelineConfiguration { MaxRetries = 2, WorkspaceBaseDirectory = "/before-update" },
            CancellationToken.None);
        var _ = await store.LoadPipelineConfigAsync(CancellationToken.None);

        // Act: update through the store
        await store.UpdatePipelineConfigAsync(
            c => c with { MaxRetries = 5, WorkspaceBaseDirectory = "/after-update" },
            CancellationToken.None);

        // The next load must return the updated value, not the previously cached "/before-update"
        var loaded = await store.LoadPipelineConfigAsync(CancellationToken.None);

        // Assert: cache was replaced with the updated value (not stale)
        loaded.WorkspaceBaseDirectory.Should().Be("/after-update");
        loaded.MaxRetries.Should().Be(5);
    }

    // ── TTL-based MemoryCache (providers, profiles, etc.) ─────────────────

    [Fact]
    public async Task ProviderConfig_Save_InvalidatesCache_CountReflectsSecondSave()
    {
        // Arrange: TTL=1ms so after Task.Delay(5) any cached entry has expired
        var store = CreateStore(ttl: TimeSpan.FromMilliseconds(1));

        var first = new ProviderConfig
        {
            Id = Guid.NewGuid().ToString(),
            Kind = ProviderKind.Issue,
            ProviderType = "GitHub",
            DisplayName = "First Provider"
        };
        await store.SaveProviderConfigAsync(first, CancellationToken.None);

        // Load to populate cache, then let TTL expire
        var afterFirst = await store.LoadProviderConfigsAsync(ProviderKind.Issue, CancellationToken.None);
        afterFirst.Should().HaveCount(1);
        await Task.Delay(5);

        // Save a second provider — must also invalidate immediately
        var second = new ProviderConfig
        {
            Id = Guid.NewGuid().ToString(),
            Kind = ProviderKind.Issue,
            ProviderType = "GitHub",
            DisplayName = "Second Provider"
        };
        await store.SaveProviderConfigAsync(second, CancellationToken.None);

        // Act
        var afterSecond = await store.LoadProviderConfigsAsync(ProviderKind.Issue, CancellationToken.None);

        // Assert: count increased — not stuck at the stale cached value of 1
        afterSecond.Should().HaveCount(2);
    }

    [Fact]
    public async Task ProviderConfig_Delete_InvalidatesCache_DeletedItemNotReturnedFromCache()
    {
        // Arrange
        var store = CreateStore(ttl: TimeSpan.FromMilliseconds(1));
        var providerId = Guid.NewGuid().ToString();
        var config = new ProviderConfig
        {
            Id = providerId,
            Kind = ProviderKind.Repository,
            ProviderType = "GitHub",
            DisplayName = "To Delete"
        };
        await store.SaveProviderConfigAsync(config, CancellationToken.None);

        // Load to populate cache — item is in the TTL cache now
        var before = await store.LoadProviderConfigsAsync(ProviderKind.Repository, CancellationToken.None);
        before.Should().ContainSingle(c => c.Id == providerId);

        // Delete — must invalidate cache even before TTL expires
        await store.DeleteProviderConfigAsync(providerId, ProviderKind.Repository, CancellationToken.None);

        // Act: load immediately (TTL may still be alive; invalidation must have fired)
        var after = await store.LoadProviderConfigsAsync(ProviderKind.Repository, CancellationToken.None);

        // Assert
        after.Should().NotContain(c => c.Id == providerId);
    }

    // ── InvalidateCaches() resets both caches ─────────────────────────────

    [Fact]
    public async Task InvalidateCaches_ClearsPipelineConfigCache_StoreSeesDirectDbWrite()
    {
        // Arrange
        var store = CreateStore();
        await store.SavePipelineConfigAsync(
            new PipelineConfiguration { MaxRetries = 1, WorkspaceBaseDirectory = "/initial" },
            CancellationToken.None);

        // Populate the permanent pipeline-config cache
        var cached = await store.LoadPipelineConfigAsync(CancellationToken.None);
        cached.WorkspaceBaseDirectory.Should().Be("/initial");

        // Write a different value directly to the DB (bypassing the store)
        await WriteDirectlyToPipelineConfigDbAsync(new PipelineConfiguration
        {
            MaxRetries = 55,
            WorkspaceBaseDirectory = "/direct-after-invalidate"
        });

        // Act: call InvalidateCaches() — this must clear _pipelineConfigCache
        store.InvalidateCaches();
        var reloaded = await store.LoadPipelineConfigAsync(CancellationToken.None);

        // Assert: the store fetched from DB, not from the now-cleared cache
        reloaded.WorkspaceBaseDirectory.Should().Be("/direct-after-invalidate");
        reloaded.MaxRetries.Should().Be(55);
    }

    [Fact]
    public async Task InvalidateCaches_ClearsProviderCache_StoreSeesDirectDbChange()
    {
        // Arrange: use a long TTL so the cache would normally survive
        var store = CreateStore(ttl: TimeSpan.FromMinutes(5));
        var providerId = Guid.NewGuid().ToString();
        var config = new ProviderConfig
        {
            Id = providerId,
            Kind = ProviderKind.Agent,
            ProviderType = "Kiro",
            DisplayName = "Agent Provider"
        };
        await store.SaveProviderConfigAsync(config, CancellationToken.None);

        // Load to populate the TTL cache
        var before = await store.LoadProviderConfigsAsync(ProviderKind.Agent, CancellationToken.None);
        before.Should().ContainSingle(c => c.Id == providerId);

        // Delete directly from DB without going through the store
        await DeleteDirectlyFromProviderConfigDbAsync(Guid.Parse(providerId));

        // Without InvalidateCaches, the store would still return the cached item.
        // Act: invalidate, then load
        store.InvalidateCaches();
        var after = await store.LoadProviderConfigsAsync(ProviderKind.Agent, CancellationToken.None);

        // Assert: deleted item is gone — cache was cleared
        after.Should().NotContain(c => c.Id == providerId);
    }

    // ── Helpers ───────────────────────────────────────────────────────────

    private async Task WriteDirectlyToPipelineConfigDbAsync(PipelineConfiguration config)
    {
        await using var db = new CacheTestPipelineDbContext(_dbOptions);
        var entity = await db.PipelineConfig.FirstOrDefaultAsync();
        var json = JsonSerializer.Serialize(config, PipelineJsonOptions.Default);
        if (entity is null)
        {
            db.PipelineConfig.Add(new PipelineConfigEntity { Id = Guid.NewGuid(), Configuration = json });
        }
        else
        {
            entity.Configuration = json;
        }
        await db.SaveChangesAsync();
    }

    private async Task DeleteDirectlyFromProviderConfigDbAsync(Guid providerId)
    {
        await using var db = new CacheTestPipelineDbContext(_dbOptions);
        var entity = await db.ProviderConfigs.FirstOrDefaultAsync(e => e.Id == providerId);
        if (entity is not null)
        {
            db.ProviderConfigs.Remove(entity);
            await db.SaveChangesAsync();
        }
    }
}

file sealed class CacheTestPipelineDbContext : PipelineDbContext
{
    public CacheTestPipelineDbContext(DbContextOptions<PipelineDbContext> options) : base(options) { }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        // Strip RowVersion and filtered indexes for InMemory compat
        foreach (var entityType in modelBuilder.Model.GetEntityTypes())
        {
            var rv = entityType.FindProperty("RowVersion");
            if (rv != null)
            {
                rv.IsConcurrencyToken = false;
                rv.ValueGenerated = Microsoft.EntityFrameworkCore.Metadata.ValueGenerated.Never;
            }
            foreach (var idx in entityType.GetIndexes().Where(i => i.GetFilter() != null).ToList())
                entityType.RemoveIndex(idx);
        }
    }
}

file sealed class CacheTestDbContextFactory : IDbContextFactory<PipelineDbContext>
{
    private readonly DbContextOptions<PipelineDbContext> _options;
    public CacheTestDbContextFactory(DbContextOptions<PipelineDbContext> options) => _options = options;
    public PipelineDbContext CreateDbContext() => new CacheTestPipelineDbContext(_options);
    public Task<PipelineDbContext> CreateDbContextAsync(CancellationToken ct = default) =>
        Task.FromResult(CreateDbContext());
}
