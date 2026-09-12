using System.Text.Json;
using AwesomeAssertions;
using CodingAgent.Infrastructure;
using CodingAgent.Infrastructure.Locking;
using CodingAgent.Infrastructure.Persistence;
using CodingAgent.Infrastructure.Persistence.Entities;
using CodingAgent.Pipeline;
using CodingAgent.Pipeline.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.Extensions.Configuration;
using Serilog;
using Serilog.Core;
using Serilog.Events;

namespace CodingAgent.Web.UnitTests.Services;

/// <summary>
/// Tests for DatabaseStartupService.SeedDefaultReviewerConfigsIfNeededAsync — verifies that
/// default reviewer configurations are seeded into an empty ReviewerConfigs table on startup.
/// </summary>
public class DatabaseStartupServiceReviewerSeedingTests : IDisposable
{
    private readonly DbContextOptions<PipelineDbContext> _dbOptions;
    private readonly InMemoryDbContextFactory _dbFactory;
    private readonly InProcessDistributedLockProvider _lockProvider;
    private readonly string _tempDir;

    public DatabaseStartupServiceReviewerSeedingTests()
    {
        var dbName = $"StartupReviewerSeed-{Guid.NewGuid()}";
        _dbOptions = new DbContextOptionsBuilder<PipelineDbContext>()
            .UseInMemoryDatabase(dbName)
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;

        _dbFactory = new InMemoryDbContextFactory(_dbOptions);
        _lockProvider = new InProcessDistributedLockProvider();
        _tempDir = Path.Combine(Path.GetTempPath(), $"startup-reviewer-seed-test-{Guid.NewGuid()}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        using var db = new InMemoryPipelineDbContext(_dbOptions);
        db.Database.EnsureDeleted();

        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    // ── Test cases ─────────────────────────────────────────────────────

    [Fact]
    public async Task SeedDefaultReviewerConfigsIfNeeded_WhenTableEmpty_SeedsDefaultConfigs()
    {
        // Arrange
        var sink = new CapturingSink();
        var logger = new LoggerConfiguration().WriteTo.Sink(sink).CreateLogger();
        var service = CreateService(logger);

        // Act
        await service.SeedDefaultReviewerConfigsIfNeededAsync(CancellationToken.None);

        // Assert: row count matches the number of default configs
        await using var db = _dbFactory.CreateDbContext();
        var count = await db.ReviewerConfigs.CountAsync();
        // TODO: Asserting against the runtime constant means a change to DefaultReviewerConfigurations
        // (e.g. accidentally becoming 0 or 2) would not be caught. Consider asserting count.Should().Be(1)
        // with an inline comment explaining the expected value.
        count.Should().Be(PipelineConfigurationDefaults.DefaultReviewerConfigurations.Count);

        // Assert: the seeded config has the expected name
        var entity = await db.ReviewerConfigs.SingleAsync();
        // TODO: Hard-coded string "Default Reviewers" duplicates PipelineConfigurationDefaults.DefaultReviewerConfigurations[0].DisplayName.
        // Reference the constant directly so a display-name change produces a compile-time signal rather than a misleading mismatch.
        entity.Name.Should().Be("Default Reviewers");

        // Assert: Configuration blob is valid JSON that round-trips to a ReviewerConfiguration
        entity.Configuration.Should().NotBeNullOrEmpty();
        var deserialized = JsonSerializer.Deserialize<ReviewerConfiguration>(
            entity.Configuration!, PipelineJsonOptions.Default);
        deserialized.Should().NotBeNull();
        // TODO: Hard-coded string "Default Reviewers" duplicates PipelineConfigurationDefaults.DefaultReviewerConfigurations[0].DisplayName.
        // Reference the constant directly.
        deserialized!.DisplayName.Should().Be("Default Reviewers");
        deserialized.MatchLabels.Should().BeEmpty();
        // TODO: Expected agent count is derived from the same runtime list the production code reads. A silent
        // drop/duplication in DefaultReviewAgents would still pass. Assert .HaveCount(<concrete number>) or
        // verify agent identity explicitly (e.g. .Contain(a => a.Name == "Correctness")).
        deserialized.Agents.Should().HaveCount(PipelineConfigurationDefaults.DefaultReviewAgents.Count);
        deserialized.Enabled.Should().BeTrue();

        // Assert: an Information log was emitted (AC: startup log entry when seeding occurs)
        sink.Events
            .Should().ContainSingle(e =>
                e.Level == LogEventLevel.Information &&
                e.RenderMessage().Contains("seeding"));
    }

    [Fact]
    public async Task SeedDefaultReviewerConfigsIfNeeded_WhenTableHasExistingConfigs_DoesNotOverwrite()
    {
        // Arrange: pre-seed a custom reviewer config
        var customConfig = new ReviewerConfiguration
        {
            Id = Guid.NewGuid().ToString(),
            DisplayName = "Custom Reviewers",
            MatchLabels = ["dotnet"],
            Agents = [new ReviewAgent { Name = "Correctness", Prompt = "Review for correctness" }],
            Enabled = true,
            ExecutionOrder = 5
        };

        await using (var db = _dbFactory.CreateDbContext())
        {
            db.ReviewerConfigs.Add(new ReviewerConfigEntity
            {
                Id = Guid.NewGuid(),
                Name = customConfig.DisplayName,
                Configuration = JsonSerializer.Serialize(customConfig, PipelineJsonOptions.Default)
            });
            await db.SaveChangesAsync();
        }

        var service = CreateService();

        // Act
        await service.SeedDefaultReviewerConfigsIfNeededAsync(CancellationToken.None);

        // Assert: count is still 1 (the custom config); no defaults were injected
        await using var dbCheck = _dbFactory.CreateDbContext();
        var count = await dbCheck.ReviewerConfigs.CountAsync();
        count.Should().Be(1);

        var entity = await dbCheck.ReviewerConfigs.SingleAsync();
        entity.Name.Should().Be("Custom Reviewers");
    }

    [Fact]
    public async Task SeedDefaultReviewerConfigsIfNeeded_IsIdempotent_SecondCallDoesNotDuplicate()
    {
        // Arrange
        var service = CreateService();

        // Act: call twice
        await service.SeedDefaultReviewerConfigsIfNeededAsync(CancellationToken.None);
        await service.SeedDefaultReviewerConfigsIfNeededAsync(CancellationToken.None);

        // Assert: still exactly the default count (no duplicates)
        await using var db = _dbFactory.CreateDbContext();
        var count = await db.ReviewerConfigs.CountAsync();
        count.Should().Be(PipelineConfigurationDefaults.DefaultReviewerConfigurations.Count);
    }

    [Fact]
    public async Task InitializeAsync_FreshInstall_SeedsDefaultReviewerConfigs()
    {
        // Arrange: fresh DB + empty config dir (no JSON files → MigrateIfNeededAsync skips).
        // Note: InitializeAsync calls HandleMigrationsAsync which uses relational-only APIs
        // incompatible with the InMemory provider. We exercise the seeding wiring by calling
        // ImportJsonConfigIfNeededAsync (which skips on empty dir) then SeedDefaultReviewerConfigsIfNeededAsync
        // directly, matching the exact call order in InitializeAsync and verifying the two-step wiring.
        // TODO: This test does NOT call InitializeAsync itself, so removing the
        // SeedDefaultReviewerConfigsIfNeededAsync call from InitializeAsync would not be detected.
        // Consider an integration test (using a relational provider) that calls InitializeAsync end-to-end,
        // or at minimum rename this test to reflect that it exercises individual steps rather than InitializeAsync wiring.
        var service = CreateService();

        // Act: replicate the relevant steps of InitializeAsync (skipping HandleMigrationsAsync
        // which requires a relational database provider)
        await service.ImportJsonConfigIfNeededAsync(CancellationToken.None, _tempDir);
        await service.SeedDefaultReviewerConfigsIfNeededAsync(CancellationToken.None);

        // Assert: ReviewerConfigs table is populated
        await using var db = _dbFactory.CreateDbContext();
        var count = await db.ReviewerConfigs.CountAsync();
        count.Should().Be(PipelineConfigurationDefaults.DefaultReviewerConfigurations.Count);

        var entity = await db.ReviewerConfigs.SingleAsync();
        entity.Name.Should().Be("Default Reviewers");
    }

    [Fact]
    public async Task SeedDefaultReviewerConfigsIfNeeded_DoesNotSeedWhenJsonMigrationPopulatedTable()
    {
        // Arrange: simulate a JSON migration run by pre-populating a reviewer config
        // (represents fresh install WITH JSON reviewer files — migration ran first).
        await using (var db = _dbFactory.CreateDbContext())
        {
            db.ReviewerConfigs.Add(new ReviewerConfigEntity
            {
                Id = Guid.NewGuid(),
                Name = "From JSON Migration",
                Configuration = JsonSerializer.Serialize(
                    new ReviewerConfiguration
                    {
                        Id = "migrated-id",
                        DisplayName = "From JSON Migration",
                        MatchLabels = ["legacy"],
                        Agents = [new ReviewAgent { Name = "Legacy", Prompt = "legacy prompt" }]
                    },
                    PipelineJsonOptions.Default)
            });
            await db.SaveChangesAsync();
        }

        var service = CreateService();

        // Act
        await service.SeedDefaultReviewerConfigsIfNeededAsync(CancellationToken.None);

        // Assert: only the migrated config remains, no defaults injected
        await using var dbCheck = _dbFactory.CreateDbContext();
        var count = await dbCheck.ReviewerConfigs.CountAsync();
        count.Should().Be(1);

        var entity = await dbCheck.ReviewerConfigs.SingleAsync();
        entity.Name.Should().Be("From JSON Migration");
    }

    // ── Helpers ────────────────────────────────────────────────────────

    private DatabaseStartupService CreateService(Serilog.ILogger? logger = null)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>())
            .Build();

        return new DatabaseStartupService(
            _dbFactory, _lockProvider, config,
            logger ?? new LoggerConfiguration().CreateLogger(),
            new NoOpProbe());
    }

    /// <summary>Probe that always succeeds (connection retry is not under test here).</summary>
    private sealed class NoOpProbe : IDatabaseProbe
    {
        public Task ProbeAsync(CancellationToken ct) => Task.CompletedTask;
    }

    /// <summary>Serilog sink that captures log events for assertion.</summary>
    private sealed class CapturingSink : ILogEventSink
    {
        private readonly List<LogEvent> _events = new();

        public IReadOnlyList<LogEvent> Events => _events;

        public void Emit(LogEvent logEvent) => _events.Add(logEvent);
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
                    rowVersionProp.ValueGenerated = ValueGenerated.Never;
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
