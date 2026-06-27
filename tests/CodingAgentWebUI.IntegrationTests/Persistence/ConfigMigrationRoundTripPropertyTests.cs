// Feature: Persistence Integration Tests
// Property 5: Config Migration Round-Trip — Import then read-back produces equivalent data
using System.Text.Json;
using AwesomeAssertions;
using CodingAgentWebUI.Infrastructure.Locking;
using CodingAgentWebUI.Infrastructure.Persistence;
using CodingAgentWebUI.Infrastructure.Persistence.Services;
using CodingAgentWebUI.Infrastructure.Persistence.Stores;
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
/// Property 5: Config Migration Round-Trip.
/// Generate valid PipelineConfiguration, write to JSON files, import via ConfigMigrationService,
/// then read back from PostgresConfigurationStore — values must be equivalent.
/// Uses InMemory EF Core provider.
/// </summary>
public class ConfigMigrationRoundTripPropertyTests : IDisposable
{
    private readonly DbContextOptions<PipelineDbContext> _dbOptions;
    private readonly InMemoryDbContextFactory _dbFactory;
    private readonly IDistributedLockProvider _lockProvider;
    private readonly string _tempDir;

    public ConfigMigrationRoundTripPropertyTests()
    {
        var dbName = $"MigrationRoundTrip-{Guid.NewGuid()}";
        _dbOptions = new DbContextOptionsBuilder<PipelineDbContext>()
            .UseInMemoryDatabase(dbName)
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;

        _dbFactory = new InMemoryDbContextFactory(_dbOptions);
        _lockProvider = new NoOpDistributedLockProvider();
        _tempDir = Path.Combine(Path.GetTempPath(), $"migration-roundtrip-{Guid.NewGuid()}");
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
    /// Property 5: Config Migration Round-Trip.
    /// For any generated PipelineConfiguration, writing it to a JSON file,
    /// importing via ConfigMigrationService, then reading back from the store
    /// produces semantically equivalent configuration.
    /// </summary>
    [Property(Arbitrary = new[] { typeof(PipelineConfigArbitraries) })]
    public async Task<bool> ImportThenReadBack_ProducesEquivalentConfig(PipelineConfiguration config)
    {
        // Reset DB for each iteration
        await using (var resetDb = new InMemoryPipelineDbContext(_dbOptions))
        {
            await resetDb.Database.EnsureDeletedAsync();
            await resetDb.Database.EnsureCreatedAsync();
        }

        // Write config to temp JSON file
        var configJson = JsonSerializer.Serialize(config, PipelineJsonOptions.Default);

        // Create a fresh temp subdir for this iteration
        var iterDir = Path.Combine(_tempDir, Guid.NewGuid().ToString());
        Directory.CreateDirectory(iterDir);
        File.WriteAllText(Path.Combine(iterDir, "pipeline-config.json"), configJson);

        // Import via ConfigMigrationService
        var migrationService = new ConfigMigrationService(_dbFactory, _lockProvider, iterDir);
        var migrated = await migrationService.MigrateIfNeededAsync(CancellationToken.None);

        if (!migrated) return false;

        // Read back via store
        var store = new PostgresConfigurationStore(_dbFactory, cacheTtl: TimeSpan.Zero);
        var loaded = await store.LoadPipelineConfigAsync(CancellationToken.None);

        // Verify key properties survive the migration round-trip
        return loaded.MaxRetries == config.MaxRetries
            && loaded.AgentTimeout == config.AgentTimeout
            && loaded.IssuePageSize == config.IssuePageSize
            && loaded.AnalysisReviewEnabled == config.AnalysisReviewEnabled
            && loaded.AcceptanceCriteriaEnabled == config.AcceptanceCriteriaEnabled
            && loaded.BaselineHealthCheckEnabled == config.BaselineHealthCheckEnabled;
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
