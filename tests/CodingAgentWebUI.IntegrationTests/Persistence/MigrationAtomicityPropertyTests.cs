// Feature: Persistence Integration Tests
// Property 7: Migration Atomicity — One invalid file among valid ones causes full rollback
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

namespace CodingAgentWebUI.IntegrationTests.Persistence;

/// <summary>
/// Property 7: Migration Atomicity.
/// When one invalid JSON file is injected among valid config files,
/// ConfigMigrationService rolls back the entire transaction — DB remains empty.
/// Uses InMemory EF Core provider.
/// </summary>
public class MigrationAtomicityPropertyTests : IDisposable
{
    private readonly DbContextOptions<PipelineDbContext> _dbOptions;
    private readonly InMemoryDbContextFactory _dbFactory;
    private readonly IDistributedLockProvider _lockProvider;
    private readonly string _tempDir;

    public MigrationAtomicityPropertyTests()
    {
        var dbName = $"MigrationAtomicity-{Guid.NewGuid()}";
        _dbOptions = new DbContextOptionsBuilder<PipelineDbContext>()
            .UseInMemoryDatabase(dbName)
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;

        _dbFactory = new InMemoryDbContextFactory(_dbOptions);
        _lockProvider = new NoOpDistributedLockProvider();
        _tempDir = Path.Combine(Path.GetTempPath(), $"migration-atomicity-{Guid.NewGuid()}");
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
    /// Property 7: Migration Atomicity.
    /// For any generated invalid JSON string injected as a provider config file,
    /// alongside a valid pipeline-config.json, the migration throws and
    /// the DB remains empty (transaction rolled back).
    /// </summary>
    [Property(MaxTest = 20)]
    public async Task<bool> InvalidJsonAmongValid_CausesFullRollback(NonEmptyString invalidJson)
    {
        // Reset DB for each iteration
        await using (var resetDb = new InMemoryPipelineDbContext(_dbOptions))
        {
            await resetDb.Database.EnsureDeletedAsync();
            await resetDb.Database.EnsureCreatedAsync();
        }

        // Create a fresh config dir per iteration
        var iterDir = Path.Combine(_tempDir, Guid.NewGuid().ToString());
        Directory.CreateDirectory(iterDir);

        // Write a valid pipeline-config.json
        var config = new PipelineConfiguration { MaxRetries = 3 };
        var configJson = JsonSerializer.Serialize(config, PipelineJsonOptions.Default);
        File.WriteAllText(Path.Combine(iterDir, "pipeline-config.json"), configJson);

        // Inject invalid JSON as a provider config file
        var providerDir = Path.Combine(iterDir, "providers", "issue");
        Directory.CreateDirectory(providerDir);
        var corruptContent = "{ " + invalidJson.Get + " invalid_json_!@#$ }}}";
        File.WriteAllText(Path.Combine(providerDir, "corrupt.json"), corruptContent);

        var service = new ConfigMigrationService(_dbFactory, _lockProvider, iterDir);

        // Migration should throw due to parse failure
        bool threwException;
        try
        {
            await service.MigrateIfNeededAsync(CancellationToken.None);
            threwException = false;
        }
        catch (InvalidOperationException)
        {
            threwException = true;
        }
        catch (JsonException)
        {
            threwException = true;
        }

        if (!threwException) return false;

        // DB should remain empty (transaction rolled back)
        await using var db = _dbFactory.CreateDbContext();
        var pipelineConfigCount = await db.PipelineConfig.CountAsync();
        var providerCount = await db.ProviderConfigs.CountAsync();

        return pipelineConfigCount == 0 && providerCount == 0;
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
