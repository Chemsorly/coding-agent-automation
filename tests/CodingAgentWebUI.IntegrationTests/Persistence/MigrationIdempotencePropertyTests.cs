// Feature: Persistence Integration Tests
// Property 6: Migration Idempotence — Running migration N times produces same state as 1 run
using System.Text.Json;
using AwesomeAssertions;
using CodingAgentWebUI.Infrastructure.Locking;
using CodingAgentWebUI.Infrastructure.Persistence;
using CodingAgentWebUI.Infrastructure.Persistence.Services;
using CodingAgentWebUI.IntegrationTests.Helpers;
using CodingAgentWebUI.Pipeline;
using CodingAgentWebUI.Pipeline.Models;
using FsCheck;
using FsCheck.Fluent;
using FsCheck.Xunit;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace CodingAgentWebUI.IntegrationTests.Persistence;

/// <summary>
/// Property 6: Migration Idempotence.
/// Running ConfigMigrationService N times (N ≥ 1) produces the same DB state
/// as running it once. The service skips if PipelineConfig row already exists.
/// Uses InMemory EF Core provider.
/// </summary>
public class MigrationIdempotencePropertyTests : IDisposable
{
    private readonly DbContextOptions<PipelineDbContext> _dbOptions;
    private readonly InMemoryDbContextFactory _dbFactory;
    private readonly IDistributedLockProvider _lockProvider;
    private readonly string _tempDir;

    public MigrationIdempotencePropertyTests()
    {
        var dbName = $"MigrationIdempotence-{Guid.NewGuid()}";
        _dbOptions = new DbContextOptionsBuilder<PipelineDbContext>()
            .UseInMemoryDatabase(dbName)
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;

        _dbFactory = new InMemoryDbContextFactory(_dbOptions);
        _lockProvider = new NoOpDistributedLockProvider();
        _tempDir = Path.Combine(Path.GetTempPath(), $"migration-idempotence-{Guid.NewGuid()}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        using var db = new InMemoryPipelineDbContext(_dbOptions);
        db.Database.EnsureDeleted();

        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    /// <summary>
    /// Property 6: Migration Idempotence.
    /// For any N in [2..5], running migration N times yields the same row count as 1 run.
    /// ConfigMigrationService returns false on subsequent runs (skipped because row exists).
    /// </summary>
    [Property]
    public async Task<bool> MigrateNTimes_ProducesSameStateAsOnce(PositiveInt repeatCount)
    {
        var n = Math.Clamp(repeatCount.Get, 2, 5);

        // Reset DB for each iteration
        await using (var resetDb = new InMemoryPipelineDbContext(_dbOptions))
        {
            await resetDb.Database.EnsureDeletedAsync();
            await resetDb.Database.EnsureCreatedAsync();
        }

        // Create a fresh config dir per iteration
        var iterDir = Path.Combine(_tempDir, Guid.NewGuid().ToString());
        Directory.CreateDirectory(iterDir);

        var config = new PipelineConfiguration { MaxRetries = 5 };
        var configJson = JsonSerializer.Serialize(config, PipelineJsonOptions.Default);
        File.WriteAllText(Path.Combine(iterDir, "pipeline-config.json"), configJson);

        var service = new ConfigMigrationService(_dbFactory, _lockProvider, iterDir);

        // First run should migrate
        var firstResult = await service.MigrateIfNeededAsync(CancellationToken.None);
        if (!firstResult) return false;

        // Get row count after first run
        int countAfterFirst;
        await using (var db = _dbFactory.CreateDbContext())
        {
            countAfterFirst = await db.PipelineConfig.CountAsync();
        }

        // Run N-1 more times — all should skip
        for (var i = 1; i < n; i++)
        {
            var subsequentResult = await service.MigrateIfNeededAsync(CancellationToken.None);
            if (subsequentResult) return false; // Should have been skipped
        }

        // Row count after N runs must equal count after 1 run
        int countAfterN;
        await using (var db = _dbFactory.CreateDbContext())
        {
            countAfterN = await db.PipelineConfig.CountAsync();
        }

        return countAfterFirst == 1 && countAfterN == 1;
    }

    // ── Test Infrastructure ─────────────────────────────────────────────

    private sealed class InMemoryPipelineDbContext : PipelineDbContext
    {
        public InMemoryPipelineDbContext(DbContextOptions<PipelineDbContext> options)
            : base(options) { }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);

            var jsonConverter = new ValueConverter<JsonDocument?, string?>(
                v => v == null ? null : v.RootElement.GetRawText(),
                v => v == null ? null : JsonDocument.Parse(v, default));

            foreach (var entityType in modelBuilder.Model.GetEntityTypes())
            {
                foreach (var property in entityType.GetProperties())
                {
                    if (property.ClrType == typeof(JsonDocument))
                    {
                        property.SetValueConverter(jsonConverter);
                        property.SetColumnType(null);
                    }
                }

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

        public InMemoryDbContextFactory(DbContextOptions<PipelineDbContext> options)
            => _options = options;

        public PipelineDbContext CreateDbContext()
            => new InMemoryPipelineDbContext(_options);

        public Task<PipelineDbContext> CreateDbContextAsync(CancellationToken ct = default)
            => Task.FromResult(CreateDbContext());
    }
}
