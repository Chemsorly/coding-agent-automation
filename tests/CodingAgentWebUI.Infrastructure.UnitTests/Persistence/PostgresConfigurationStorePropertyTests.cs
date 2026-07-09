using FsCheck;
using FsCheck.Xunit;
using CodingAgentWebUI.Infrastructure.Persistence;
using CodingAgentWebUI.Infrastructure.Persistence.Stores;
using CodingAgentWebUI.Pipeline;
using CodingAgentWebUI.Pipeline.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Xunit;

namespace CodingAgentWebUI.Infrastructure.UnitTests.Persistence;

/// <summary>
/// Property-based tests for <see cref="PostgresConfigurationStore"/> verifying
/// round-trip invariants equivalent to <see cref="ConfigurationStorePropertyTests"/>
/// (which only covers <see cref="JsonConfigurationStore"/>).
/// Uses InMemory EF Core provider as a test double for Postgres.
/// </summary>
public class PostgresConfigurationStorePropertyTests : IDisposable
{
    private readonly DbContextOptions<PipelineDbContext> _dbOptions;

    public PostgresConfigurationStorePropertyTests()
    {
        var dbName = $"PgConfigStorePbt-{Guid.NewGuid()}";
        _dbOptions = new DbContextOptionsBuilder<PipelineDbContext>()
            .UseInMemoryDatabase(dbName)
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;

        using var ctx = new InMemoryPipelineDbContext(_dbOptions);
        ctx.Database.EnsureCreated();
    }

    public void Dispose()
    {
        using var db = new InMemoryPipelineDbContext(_dbOptions);
        db.Database.EnsureDeleted();
    }

    private PostgresConfigurationStore CreateStore()
    {
        var factory = new InMemoryDbContextFactory(_dbOptions);
        return new PostgresConfigurationStore(factory, cacheTtl: TimeSpan.FromMilliseconds(1));
    }

    // ── Property P1: PipelineConfiguration round-trip ────────────────────

    /// <summary>
    /// Property P1: Saving then loading a PipelineConfiguration via Postgres store
    /// produces an equivalent object. Mirrors the Json store Property 8a.
    /// </summary>
    [Property(MaxTest = 20)]
    public void PipelineConfig_RoundTrip_PreservesData(
        int maxRetries,
        NonEmptyString workspaceDir)
    {
        var clampedRetries = Math.Clamp(Math.Abs(maxRetries), 0, 100);
        var timeoutMinutes = Math.Clamp(Math.Abs(maxRetries % 120) + 1, 1, 120);
        var original = new PipelineConfiguration
        {
            MaxRetries = clampedRetries,
            AgentTimeout = TimeSpan.FromMinutes(timeoutMinutes),
            WorkspaceBaseDirectory = workspaceDir.Get,
            BlacklistedPaths = new[] { ".agent", ".github", $".custom-{Math.Abs(maxRetries % 10)}" }
        };

        var store = CreateStore();
        store.SavePipelineConfigAsync(original, CancellationToken.None).GetAwaiter().GetResult();

        // Create fresh store to bypass cache
        var freshStore = CreateStore();
        var loaded = freshStore.LoadPipelineConfigAsync(CancellationToken.None).GetAwaiter().GetResult();

        Assert.Equal(original.MaxRetries, loaded.MaxRetries);
        Assert.Equal(original.AgentTimeout, loaded.AgentTimeout);
        Assert.Equal(original.WorkspaceBaseDirectory, loaded.WorkspaceBaseDirectory);
        Assert.Equal(original.BlacklistedPaths, loaded.BlacklistedPaths);
    }

    // ── Property P2: ProviderConfig round-trip ───────────────────────────

    /// <summary>
    /// Property P2: Saving then loading a ProviderConfig via Postgres store
    /// produces an equivalent object. Mirrors the Json store Property 8b.
    /// </summary>
    [Property(MaxTest = 20)]
    public void ProviderConfig_RoundTrip_PreservesData(
        byte kindSeed,
        NonEmptyString providerType,
        NonEmptyString displayName,
        NonEmptyString settingKey,
        NonEmptyString settingValue)
    {
        var kinds = Enum.GetValues<ProviderKind>();
        var kind = kinds[kindSeed % kinds.Length];

        var id = Guid.NewGuid().ToString();
        var original = new ProviderConfig
        {
            Id = id,
            Kind = kind,
            ProviderType = providerType.Get,
            DisplayName = displayName.Get,
            Settings = new Dictionary<string, string>
            {
                [settingKey.Get] = settingValue.Get
            }
        };

        var store = CreateStore();
        store.SaveProviderConfigAsync(original, CancellationToken.None).GetAwaiter().GetResult();

        var freshStore = CreateStore();
        var loaded = freshStore.LoadProviderConfigsAsync(kind, CancellationToken.None).GetAwaiter().GetResult();

        var match = Assert.Single(loaded, c => c.Id == id);
        Assert.Equal(original.Kind, match.Kind);
        Assert.Equal(original.ProviderType, match.ProviderType);
        Assert.Equal(original.DisplayName, match.DisplayName);
        Assert.Equal(original.Settings[settingKey.Get], match.Settings[settingKey.Get]);
    }

    // ── Property P3: Save is idempotent (upsert) ─────────────────────────

    /// <summary>
    /// Property P3: Saving the same PipelineConfiguration twice does not create duplicates
    /// and the second save's values are what get loaded.
    /// </summary>
    [Property(MaxTest = 20)]
    public void PipelineConfig_SaveTwice_IsIdempotent(int seed)
    {
        var retries1 = Math.Abs(seed % 50);
        var retries2 = Math.Abs((seed + 7) % 50);

        var store = CreateStore();

        var config1 = new PipelineConfiguration { MaxRetries = retries1 };
        store.SavePipelineConfigAsync(config1, CancellationToken.None).GetAwaiter().GetResult();

        var config2 = new PipelineConfiguration { MaxRetries = retries2 };
        store.SavePipelineConfigAsync(config2, CancellationToken.None).GetAwaiter().GetResult();

        var freshStore = CreateStore();
        var loaded = freshStore.LoadPipelineConfigAsync(CancellationToken.None).GetAwaiter().GetResult();

        Assert.Equal(retries2, loaded.MaxRetries);
    }

    // ── Property P4: ProviderConfig save then delete yields empty ─────────

    /// <summary>
    /// Property P4: Saving then deleting a ProviderConfig results in an empty list for that kind.
    /// </summary>
    [Property(MaxTest = 20)]
    public void ProviderConfig_SaveThenDelete_YieldsEmpty(byte kindSeed)
    {
        var kinds = Enum.GetValues<ProviderKind>();
        var kind = kinds[kindSeed % kinds.Length];
        var id = Guid.NewGuid().ToString();

        var config = new ProviderConfig
        {
            Id = id,
            Kind = kind,
            ProviderType = "TestProvider",
            DisplayName = "Delete Test",
            Settings = new Dictionary<string, string> { ["k"] = "v" }
        };

        var store = CreateStore();
        store.SaveProviderConfigAsync(config, CancellationToken.None).GetAwaiter().GetResult();
        store.DeleteProviderConfigAsync(id, kind, CancellationToken.None).GetAwaiter().GetResult();

        var freshStore = CreateStore();
        var loaded = freshStore.LoadProviderConfigsAsync(kind, CancellationToken.None).GetAwaiter().GetResult();

        Assert.DoesNotContain(loaded, c => c.Id == id);
    }

    // ── Property P5: GetProviderConfigById returns saved config ───────────

    /// <summary>
    /// Property P5: GetProviderConfigByIdAsync returns the same config that was saved.
    /// </summary>
    [Property(MaxTest = 20)]
    public void ProviderConfig_GetById_ReturnsSavedConfig(
        byte kindSeed,
        NonEmptyString displayName)
    {
        var kinds = Enum.GetValues<ProviderKind>();
        var kind = kinds[kindSeed % kinds.Length];
        var id = Guid.NewGuid().ToString();

        var config = new ProviderConfig
        {
            Id = id,
            Kind = kind,
            ProviderType = "TestProvider",
            DisplayName = displayName.Get,
            Settings = new Dictionary<string, string> { ["x"] = "y" }
        };

        var store = CreateStore();
        store.SaveProviderConfigAsync(config, CancellationToken.None).GetAwaiter().GetResult();

        var freshStore = CreateStore();
        var loaded = freshStore.GetProviderConfigByIdAsync(id, kind, CancellationToken.None).GetAwaiter().GetResult();

        Assert.NotNull(loaded);
        Assert.Equal(config.DisplayName, loaded!.DisplayName);
        Assert.Equal(config.ProviderType, loaded.ProviderType);
    }

    // ── Test Infrastructure ──────────────────────────────────────────────

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
                {
                    entityType.RemoveIndex(index);
                }
            }
        }
    }

    private sealed class InMemoryDbContextFactory : IDbContextFactory<PipelineDbContext>
    {
        private readonly DbContextOptions<PipelineDbContext> _options;
        public InMemoryDbContextFactory(DbContextOptions<PipelineDbContext> options) => _options = options;
        public PipelineDbContext CreateDbContext() => new InMemoryPipelineDbContext(_options);
        public Task<PipelineDbContext> CreateDbContextAsync(CancellationToken ct = default)
            => Task.FromResult(CreateDbContext());
    }
}
