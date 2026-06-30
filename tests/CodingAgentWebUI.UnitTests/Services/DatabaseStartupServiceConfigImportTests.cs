using System.Text.Json;
using AwesomeAssertions;
using CodingAgentWebUI.Infrastructure.Locking;
using CodingAgentWebUI.Infrastructure.Persistence;
using CodingAgentWebUI.Infrastructure.Persistence.Entities;
using CodingAgentWebUI.Pipeline;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Serilog;

namespace CodingAgentWebUI.UnitTests.Services;

/// <summary>
/// Tests for DatabaseStartupService.ImportJsonConfigIfNeededAsync — verifies that
/// JSON config files are imported into an empty database on startup.
/// </summary>
public class DatabaseStartupServiceConfigImportTests : IDisposable
{
    private readonly DbContextOptions<PipelineDbContext> _dbOptions;
    private readonly InMemoryDbContextFactory _dbFactory;
    private readonly InProcessDistributedLockProvider _lockProvider;
    private readonly Serilog.ILogger _logger = new LoggerConfiguration().CreateLogger();
    private readonly string _tempDir;

    public DatabaseStartupServiceConfigImportTests()
    {
        var dbName = $"StartupConfigImport-{Guid.NewGuid()}";
        _dbOptions = new DbContextOptionsBuilder<PipelineDbContext>()
            .UseInMemoryDatabase(dbName)
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;

        _dbFactory = new InMemoryDbContextFactory(_dbOptions);
        _lockProvider = new InProcessDistributedLockProvider();
        _tempDir = Path.Combine(Path.GetTempPath(), $"startup-import-test-{Guid.NewGuid()}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        using var db = new InMemoryPipelineDbContext(_dbOptions);
        db.Database.EnsureDeleted();

        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    [Fact]
    public async Task ImportJsonConfigIfNeeded_WhenDbEmpty_ImportsJsonFiles()
    {
        // Arrange: write a pipeline config JSON file
        var config = new PipelineConfiguration { MaxRetries = 7 };
        WritePipelineConfig(config);

        var service = CreateService();

        // Act
        await service.ImportJsonConfigIfNeededAsync(CancellationToken.None, _tempDir);

        // Assert: DB now has the config row
        await using var db = _dbFactory.CreateDbContext();
        var entity = await db.PipelineConfig.SingleAsync();
        entity.Configuration.Should().NotBeNull();

        var deserialized = JsonSerializer.Deserialize<PipelineConfiguration>(
            entity.Configuration!, PipelineJsonOptions.Default);
        deserialized!.MaxRetries.Should().Be(7);
    }

    [Fact]
    public async Task ImportJsonConfigIfNeeded_WhenDbHasData_SkipsImport()
    {
        // Arrange: pre-seed a PipelineConfig row
        await using (var db = _dbFactory.CreateDbContext())
        {
            db.PipelineConfig.Add(new PipelineConfigEntity
            {
                Id = Guid.NewGuid(),
                Configuration = JsonSerializer.Serialize(
                    new PipelineConfiguration { MaxRetries = 3 }, PipelineJsonOptions.Default)
            });
            await db.SaveChangesAsync();
        }

        // Write a config file that should NOT be imported
        WritePipelineConfig(new PipelineConfiguration { MaxRetries = 99 });

        var service = CreateService();

        // Act
        await service.ImportJsonConfigIfNeededAsync(CancellationToken.None, _tempDir);

        // Assert: original value preserved, file not imported
        await using var dbCheck = _dbFactory.CreateDbContext();
        var entity = await dbCheck.PipelineConfig.SingleAsync();
        var deserialized = JsonSerializer.Deserialize<PipelineConfiguration>(
            entity.Configuration!, PipelineJsonOptions.Default);
        deserialized!.MaxRetries.Should().Be(3);
    }

    [Fact]
    public async Task ImportJsonConfigIfNeeded_WhenNoConfigDirectory_SkipsGracefully()
    {
        var nonExistentPath = Path.Combine(_tempDir, "does-not-exist");
        var service = CreateService();

        // Act — should not throw
        await service.ImportJsonConfigIfNeededAsync(CancellationToken.None, nonExistentPath);

        // Assert: DB remains empty
        await using var db = _dbFactory.CreateDbContext();
        var count = await db.PipelineConfig.CountAsync();
        count.Should().Be(0);
    }

    [Fact]
    public async Task ImportJsonConfigIfNeeded_WhenDirectoryEmpty_SkipsGracefully()
    {
        // _tempDir exists but has no JSON files
        var service = CreateService();

        // Act
        await service.ImportJsonConfigIfNeededAsync(CancellationToken.None, _tempDir);

        // Assert: DB remains empty
        await using var db = _dbFactory.CreateDbContext();
        var count = await db.PipelineConfig.CountAsync();
        count.Should().Be(0);
    }

    [Fact]
    public async Task ImportJsonConfigIfNeeded_IsIdempotent_SecondCallSkips()
    {
        WritePipelineConfig(new PipelineConfiguration { MaxRetries = 5 });

        var service = CreateService();

        // Act: call twice
        await service.ImportJsonConfigIfNeededAsync(CancellationToken.None, _tempDir);
        await service.ImportJsonConfigIfNeededAsync(CancellationToken.None, _tempDir);

        // Assert: still only one row
        await using var db = _dbFactory.CreateDbContext();
        var count = await db.PipelineConfig.CountAsync();
        count.Should().Be(1);
    }

    // ── Helpers ────────────────────────────────────────────────────────

    private DatabaseStartupService CreateService()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>())
            .Build();

        return new DatabaseStartupService(_dbFactory, _lockProvider, config, _logger, new NoOpProbe());
    }

    private void WritePipelineConfig(PipelineConfiguration config)
    {
        var json = JsonSerializer.Serialize(config, PipelineJsonOptions.Default);
        File.WriteAllText(Path.Combine(_tempDir, "pipeline-config.json"), json);
    }

    /// <summary>Probe that always succeeds (connection retry is not under test here).</summary>
    private sealed class NoOpProbe : IDatabaseProbe
    {
        public Task ProbeAsync(CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class InMemoryDbContextFactory : IDbContextFactory<PipelineDbContext>
    {
        private readonly DbContextOptions<PipelineDbContext> _options;
        public InMemoryDbContextFactory(DbContextOptions<PipelineDbContext> options) => _options = options;
        public PipelineDbContext CreateDbContext() => new InMemoryPipelineDbContext(_options);
        public Task<PipelineDbContext> CreateDbContextAsync(CancellationToken ct = default)
            => Task.FromResult(CreateDbContext());
    }

    private sealed class InMemoryPipelineDbContext : PipelineDbContext
    {
        public InMemoryPipelineDbContext(DbContextOptions<PipelineDbContext> options) : base(options) { }

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
                    entityType.RemoveIndex(index);
            }
        }
    }
}
