using System.Text.Json;
using AwesomeAssertions;
using CodingAgentWebUI.Infrastructure.Locking;
using CodingAgentWebUI.Infrastructure.Persistence;
using CodingAgentWebUI.Infrastructure.Persistence.Services;
using CodingAgentWebUI.Pipeline;
using CodingAgentWebUI.Pipeline.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Xunit;

namespace CodingAgentWebUI.Infrastructure.UnitTests.Persistence;

/// <summary>
/// Unit tests for ConfigMigrationService.
/// Validates: Requirements 2.1, 2.2, 2.3, 2.4, 2.5, 2.6, 2.9
/// </summary>
public class ConfigMigrationServiceTests : IDisposable
{
    private readonly DbContextOptions<PipelineDbContext> _dbOptions;
    private readonly InMemoryDbContextFactory _dbFactory;
    private readonly InProcessDistributedLockProvider _lockProvider;
    private readonly string _tempDir;

    public ConfigMigrationServiceTests()
    {
        var dbName = $"MigrationTests-{Guid.NewGuid()}";
        _dbOptions = new DbContextOptionsBuilder<PipelineDbContext>()
            .UseInMemoryDatabase(dbName)
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;

        _dbFactory = new InMemoryDbContextFactory(_dbOptions);
        _lockProvider = new InProcessDistributedLockProvider();
        _tempDir = Path.Combine(Path.GetTempPath(), $"config-migration-test-{Guid.NewGuid()}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        using var db = new InMemoryPipelineDbContext(_dbOptions);
        db.Database.EnsureDeleted();

        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    // ── Idempotency ────────────────────────────────────────────────────

    [Fact]
    public async Task MigrateIfNeeded_WhenPipelineConfigExists_SkipsMigration()
    {
        // Seed a PipelineConfig row
        await using var db = _dbFactory.CreateDbContext();
        db.PipelineConfig.Add(new CodingAgentWebUI.Infrastructure.Persistence.Entities.PipelineConfigEntity
        {
            Id = Guid.NewGuid(),
            Configuration = SerializeToDocument(new PipelineConfiguration())
        });
        await db.SaveChangesAsync();

        // Write a config file that should NOT be imported
        WritePipelineConfig(new PipelineConfiguration { MaxRetries = 99 });

        var service = CreateService();
        var migrated = await service.MigrateIfNeededAsync(CancellationToken.None);

        migrated.Should().BeFalse();
    }

    [Fact]
    public async Task MigrateIfNeeded_WhenDirectoryMissing_SkipsMigration()
    {
        var service = new ConfigMigrationService(
            _dbFactory, _lockProvider,
            Path.Combine(_tempDir, "nonexistent"));

        var migrated = await service.MigrateIfNeededAsync(CancellationToken.None);

        migrated.Should().BeFalse();
    }

    [Fact]
    public async Task MigrateIfNeeded_WhenDirectoryEmpty_SkipsMigration()
    {
        // _tempDir exists but has no JSON files
        var service = CreateService();
        var migrated = await service.MigrateIfNeededAsync(CancellationToken.None);

        migrated.Should().BeFalse();
    }

    // ── Successful migration ───────────────────────────────────────────

    [Fact]
    public async Task MigrateIfNeeded_ImportsPipelineConfig()
    {
        var config = new PipelineConfiguration
        {
            MaxRetries = 5,
            AgentTimeout = TimeSpan.FromMinutes(45)
        };
        WritePipelineConfig(config);

        var service = CreateService();
        var migrated = await service.MigrateIfNeededAsync(CancellationToken.None);

        migrated.Should().BeTrue();

        await using var db = _dbFactory.CreateDbContext();
        var entity = await db.PipelineConfig.SingleAsync();
        entity.Configuration.Should().NotBeNull();

        var deserialized = JsonSerializer.Deserialize<PipelineConfiguration>(
            entity.Configuration!.RootElement.GetRawText(), PipelineJsonOptions.Default);
        deserialized!.MaxRetries.Should().Be(5);
    }

    [Fact]
    public async Task MigrateIfNeeded_ImportsProviderConfigs()
    {
        WritePipelineConfig(new PipelineConfiguration());

        var providerId = Guid.NewGuid().ToString();
        WriteProviderConfig("issue", new ProviderConfig
        {
            Id = providerId,
            Kind = ProviderKind.Issue,
            ProviderType = "GitHub",
            DisplayName = "Test Issue Provider"
        });

        var service = CreateService();
        await service.MigrateIfNeededAsync(CancellationToken.None);

        await using var db = _dbFactory.CreateDbContext();
        var providers = await db.ProviderConfigs.ToListAsync();
        providers.Should().ContainSingle();
        providers[0].Kind.Should().Be(ProviderKind.Issue);
        providers[0].DisplayName.Should().Be("Test Issue Provider");
    }

    [Fact]
    public async Task MigrateIfNeeded_ImportsAgentProfiles()
    {
        WritePipelineConfig(new PipelineConfiguration());

        var profileId = Guid.NewGuid().ToString();
        WriteJsonFile("profiles", $"{profileId}.json", new AgentProfile
        {
            Id = profileId,
            DisplayName = "DotNet Profile",
            AgentProviderConfigId = "agent-1",
            MatchLabels = ["dotnet"],
            Enabled = true,
            Priority = 1
        });

        var service = CreateService();
        await service.MigrateIfNeededAsync(CancellationToken.None);

        await using var db = _dbFactory.CreateDbContext();
        var profiles = await db.AgentProfiles.ToListAsync();
        profiles.Should().ContainSingle();
        profiles[0].Name.Should().Be("DotNet Profile");
    }

    [Fact]
    public async Task MigrateIfNeeded_ImportsQualityGateConfigs()
    {
        WritePipelineConfig(new PipelineConfiguration());

        var qgId = Guid.NewGuid().ToString();
        WriteJsonFile("quality-gates", $"{qgId}.json", new QualityGateConfiguration
        {
            Id = qgId,
            DisplayName = "Build Gate",
            CompilationCommand = "dotnet",
            CompilationArguments = ["build"]
        });

        var service = CreateService();
        await service.MigrateIfNeededAsync(CancellationToken.None);

        await using var db = _dbFactory.CreateDbContext();
        var gates = await db.QualityGateConfigs.ToListAsync();
        gates.Should().ContainSingle();
        gates[0].Name.Should().Be("Build Gate");
    }

    [Fact]
    public async Task MigrateIfNeeded_ImportsReviewerConfigs()
    {
        WritePipelineConfig(new PipelineConfiguration());

        var revId = Guid.NewGuid().ToString();
        WriteJsonFile("reviewers", $"{revId}.json", new ReviewerConfiguration
        {
            Id = revId,
            DisplayName = "Security Review",
            Agents = [new ReviewAgent { Name = "SecBot", Prompt = "Review" }]
        });

        var service = CreateService();
        await service.MigrateIfNeededAsync(CancellationToken.None);

        await using var db = _dbFactory.CreateDbContext();
        var reviewers = await db.ReviewerConfigs.ToListAsync();
        reviewers.Should().ContainSingle();
        reviewers[0].Name.Should().Be("Security Review");
    }

    [Fact]
    public async Task MigrateIfNeeded_ImportsProjectsAndTemplates()
    {
        WritePipelineConfig(new PipelineConfiguration());

        var projectId = Guid.NewGuid().ToString();
        var templateId = Guid.NewGuid().ToString();

        WriteJsonFile("projects", $"{projectId}.json", new PipelineProject
        {
            Id = projectId,
            Name = "MyProject",
            Enabled = true,
            TemplateIds = [templateId]
        });

        // Template under projects/{id}/templates/
        var templatesDir = Path.Combine(_tempDir, "projects", projectId, "templates");
        Directory.CreateDirectory(templatesDir);
        var templateJson = JsonSerializer.Serialize(new PipelineJobTemplate
        {
            Id = templateId,
            Name = "Main Template",
            IssueProviderId = "issue-1",
            RepoProviderId = "repo-1"
        }, PipelineJsonOptions.Default);
        File.WriteAllText(Path.Combine(templatesDir, $"{templateId}.json"), templateJson);

        var service = CreateService();
        await service.MigrateIfNeededAsync(CancellationToken.None);

        await using var db = _dbFactory.CreateDbContext();
        var projects = await db.Projects.ToListAsync();
        projects.Should().ContainSingle();
        projects[0].Name.Should().Be("MyProject");

        var templates = await db.PipelineJobTemplates.ToListAsync();
        templates.Should().ContainSingle();
        templates[0].Name.Should().Be("Main Template");
        templates[0].ProjectId.Should().Be(Guid.Parse(projectId));
    }

    [Fact]
    public async Task MigrateIfNeeded_ImportsConsolidationRuns()
    {
        WritePipelineConfig(new PipelineConfiguration());

        var runId = Guid.NewGuid().ToString();
        WriteJsonFile("consolidation-runs", $"{runId}.json", new ConsolidationRun
        {
            RunId = runId,
            Type = ConsolidationRunType.BrainConsolidation,
            StartedAtUtc = DateTime.UtcNow,
            Status = ConsolidationRunStatus.Succeeded
        });

        var service = CreateService();
        await service.MigrateIfNeededAsync(CancellationToken.None);

        await using var db = _dbFactory.CreateDbContext();
        var runs = await db.ConsolidationRuns.ToListAsync();
        runs.Should().ContainSingle();
        runs[0].Id.Should().Be(Guid.Parse(runId));
    }

    [Fact]
    public async Task MigrateIfNeeded_ImportsPipelineRuns()
    {
        WritePipelineConfig(new PipelineConfiguration());

        var runId = Guid.NewGuid().ToString();
        var startedAt = new DateTime(2024, 6, 15, 10, 0, 0, DateTimeKind.Utc);
        WriteJsonFile("runs", $"{runId}.json", new PipelineRunSummary
        {
            RunId = runId,
            IssueIdentifier = "org/repo#42",
            IssueTitle = "Fix bug",
            FinalStep = PipelineStep.Completed,
            StartedAt = startedAt,
            StartedAtOffset = new DateTimeOffset(startedAt, TimeSpan.Zero),
            RetryCount = 1,
            PullRequestUrl = "https://github.com/org/repo/pull/43",
            RunType = PipelineRunType.Implementation
        });

        var service = CreateService();
        await service.MigrateIfNeededAsync(CancellationToken.None);

        await using var db = _dbFactory.CreateDbContext();
        var runs = await db.PipelineRuns.ToListAsync();
        runs.Should().ContainSingle();
        runs[0].IssueIdentifier.Should().Be("org/repo#42");
        runs[0].FinalStep.Should().Be(PipelineStep.Completed);
        runs[0].PullRequestUrl.Should().Be("https://github.com/org/repo/pull/43");
        runs[0].WorkItemId.Should().BeNull(); // migrated runs have no work item
    }

    // ── Atomicity (rollback on failure) ────────────────────────────────

    [Fact]
    public async Task MigrateIfNeeded_OnParseFailure_RollsBackTransaction()
    {
        WritePipelineConfig(new PipelineConfiguration());

        // Write invalid JSON in a provider file
        var providerDir = Path.Combine(_tempDir, "providers", "issue");
        Directory.CreateDirectory(providerDir);
        File.WriteAllText(Path.Combine(providerDir, "bad.json"), "{ invalid json }}}");

        var service = CreateService();

        var act = () => service.MigrateIfNeededAsync(CancellationToken.None);
        await act.Should().ThrowAsync<InvalidOperationException>();

        // DB should remain empty (rolled back)
        await using var db = _dbFactory.CreateDbContext();
        var configCount = await db.PipelineConfig.CountAsync();
        configCount.Should().Be(0);
    }

    // ── Second run is idempotent ───────────────────────────────────────

    [Fact]
    public async Task MigrateIfNeeded_SecondRun_SkipsBecausePipelineConfigExists()
    {
        WritePipelineConfig(new PipelineConfiguration { MaxRetries = 7 });

        var service = CreateService();
        var first = await service.MigrateIfNeededAsync(CancellationToken.None);
        var second = await service.MigrateIfNeededAsync(CancellationToken.None);

        first.Should().BeTrue();
        second.Should().BeFalse();

        // Verify no duplication
        await using var db = _dbFactory.CreateDbContext();
        var count = await db.PipelineConfig.CountAsync();
        count.Should().Be(1);
    }

    // ── Helpers ────────────────────────────────────────────────────────

    private ConfigMigrationService CreateService()
        => new(_dbFactory, _lockProvider, _tempDir);

    private void WritePipelineConfig(PipelineConfiguration config)
    {
        var json = JsonSerializer.Serialize(config, PipelineJsonOptions.Default);
        File.WriteAllText(Path.Combine(_tempDir, "pipeline-config.json"), json);
    }

    private void WriteProviderConfig(string kindDir, ProviderConfig config)
    {
        var dir = Path.Combine(_tempDir, "providers", kindDir);
        Directory.CreateDirectory(dir);
        var json = JsonSerializer.Serialize(config, PipelineJsonOptions.Default);
        File.WriteAllText(Path.Combine(dir, $"{config.Id}.json"), json);
    }

    private void WriteJsonFile<T>(string subDir, string fileName, T value)
    {
        var dir = Path.Combine(_tempDir, subDir);
        Directory.CreateDirectory(dir);
        var json = JsonSerializer.Serialize(value, PipelineJsonOptions.Default);
        File.WriteAllText(Path.Combine(dir, fileName), json);
    }

    private static JsonDocument SerializeToDocument<T>(T value)
    {
        var json = JsonSerializer.Serialize(value, PipelineJsonOptions.Default);
        return JsonDocument.Parse(json);
    }

    /// <summary>
    /// IDbContextFactory implementation backed by InMemory provider for unit testing.
    /// </summary>
    internal sealed class InMemoryDbContextFactory : IDbContextFactory<PipelineDbContext>
    {
        private readonly DbContextOptions<PipelineDbContext> _options;

        public InMemoryDbContextFactory(DbContextOptions<PipelineDbContext> options)
            => _options = options;

        public PipelineDbContext CreateDbContext()
            => new InMemoryPipelineDbContext(_options);

        public Task<PipelineDbContext> CreateDbContextAsync(CancellationToken ct = default)
            => Task.FromResult(CreateDbContext());
    }

    /// <summary>
    /// Custom PipelineDbContext subclass that configures value converters for JsonDocument
    /// (not natively supported by InMemory provider) and removes Postgres-specific configs.
    /// </summary>
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
}
