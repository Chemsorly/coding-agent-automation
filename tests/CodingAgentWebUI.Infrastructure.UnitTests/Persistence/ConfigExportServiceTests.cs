using System.Text.Json;
using AwesomeAssertions;
using CodingAgentWebUI.Infrastructure.Persistence;
using CodingAgentWebUI.Infrastructure.Persistence.Entities;
using CodingAgentWebUI.Infrastructure.Persistence.Services;
using CodingAgentWebUI.Pipeline;
using CodingAgentWebUI.Pipeline.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Xunit;

namespace CodingAgentWebUI.Infrastructure.UnitTests.Persistence;

/// <summary>
/// Unit tests for ConfigExportService.
/// Validates: Requirement 2.10 — export DB state back to JSON file format.
/// </summary>
public class ConfigExportServiceTests : IDisposable
{
    private readonly DbContextOptions<PipelineDbContext> _dbOptions;
    private readonly InMemoryDbContextFactory _dbFactory;
    private readonly string _outputDir;

    public ConfigExportServiceTests()
    {
        var dbName = $"ExportTests-{Guid.NewGuid()}";
        _dbOptions = new DbContextOptionsBuilder<PipelineDbContext>()
            .UseInMemoryDatabase(dbName)
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;

        _dbFactory = new InMemoryDbContextFactory(_dbOptions);
        _outputDir = Path.Combine(Path.GetTempPath(), $"config-export-test-{Guid.NewGuid()}");
        Directory.CreateDirectory(_outputDir);
    }

    public void Dispose()
    {
        using var db = new InMemoryPipelineDbContext(_dbOptions);
        db.Database.EnsureDeleted();

        if (Directory.Exists(_outputDir))
            Directory.Delete(_outputDir, recursive: true);
    }

    [Fact]
    public async Task ExportAsync_ExportsPipelineConfig()
    {
        // Arrange
        var config = new PipelineConfiguration { MaxRetries = 7 };
        await SeedPipelineConfig(config);

        var service = new ConfigExportService(_dbFactory);

        // Act
        await service.ExportAsync(_outputDir, CancellationToken.None);

        // Assert
        var path = Path.Combine(_outputDir, "pipeline-config.json");
        File.Exists(path).Should().BeTrue();

        var json = await File.ReadAllTextAsync(path);
        var deserialized = JsonSerializer.Deserialize<PipelineConfiguration>(json, PipelineJsonOptions.Default);
        deserialized!.MaxRetries.Should().Be(7);
    }

    [Fact]
    public async Task ExportAsync_ExportsProviderConfigs_GroupedByKind()
    {
        // Arrange
        var issueProvider = new ProviderConfig
        {
            Id = Guid.NewGuid().ToString(),
            DisplayName = "GitHub Issues",
            Kind = ProviderKind.Issue,
            ProviderType = "GitHub"
        };
        var repoProvider = new ProviderConfig
        {
            Id = Guid.NewGuid().ToString(),
            DisplayName = "GitHub Repos",
            Kind = ProviderKind.Repository,
            ProviderType = "GitHub"
        };

        await using var db = _dbFactory.CreateDbContext();
        db.ProviderConfigs.Add(new ProviderConfigEntity
        {
            Id = Guid.Parse(issueProvider.Id),
            Kind = ProviderKind.Issue,
            DisplayName = issueProvider.DisplayName,
            ProviderType = issueProvider.ProviderType,
            Enabled = true,
            Configuration = SerializeToDocument(issueProvider)
        });
        db.ProviderConfigs.Add(new ProviderConfigEntity
        {
            Id = Guid.Parse(repoProvider.Id),
            Kind = ProviderKind.Repository,
            DisplayName = repoProvider.DisplayName,
            ProviderType = repoProvider.ProviderType,
            Enabled = true,
            Configuration = SerializeToDocument(repoProvider)
        });
        await db.SaveChangesAsync();

        var service = new ConfigExportService(_dbFactory);

        // Act
        await service.ExportAsync(_outputDir, CancellationToken.None);

        // Assert
        var issuePath = Path.Combine(_outputDir, "providers", "issue", $"{issueProvider.Id}.json");
        var repoPath = Path.Combine(_outputDir, "providers", "repository", $"{repoProvider.Id}.json");

        File.Exists(issuePath).Should().BeTrue();
        File.Exists(repoPath).Should().BeTrue();

        var issueJson = await File.ReadAllTextAsync(issuePath);
        var deserialized = JsonSerializer.Deserialize<ProviderConfig>(issueJson, PipelineJsonOptions.Default);
        deserialized!.DisplayName.Should().Be("GitHub Issues");
    }

    [Fact]
    public async Task ExportAsync_ExportsAgentProfiles()
    {
        // Arrange
        var profileId = Guid.NewGuid();
        var profile = new AgentProfile { Id = profileId.ToString(), DisplayName = "Test Profile", AgentProviderConfigId = "provider-1" };

        await using var db = _dbFactory.CreateDbContext();
        db.AgentProfiles.Add(new AgentProfileEntity
        {
            Id = profileId,
            Name = profile.DisplayName,
            Configuration = SerializeToDocument(profile)
        });
        await db.SaveChangesAsync();

        var service = new ConfigExportService(_dbFactory);

        // Act
        await service.ExportAsync(_outputDir, CancellationToken.None);

        // Assert
        var path = Path.Combine(_outputDir, "profiles", $"{profileId}.json");
        File.Exists(path).Should().BeTrue();

        var json = await File.ReadAllTextAsync(path);
        var deserialized = JsonSerializer.Deserialize<AgentProfile>(json, PipelineJsonOptions.Default);
        deserialized!.DisplayName.Should().Be("Test Profile");
    }

    [Fact]
    public async Task ExportAsync_ExportsProjectsWithTemplates()
    {
        // Arrange
        var projectId = Guid.NewGuid();
        var templateId = Guid.NewGuid();

        var project = new PipelineProject
        {
            Id = projectId.ToString(),
            Name = "My Project",
            Enabled = true,
            Description = "Test project"
        };

        var template = new PipelineJobTemplate
        {
            Id = templateId.ToString(),
            Name = "Template A",
            IssueProviderId = "issue-1",
            RepoProviderId = "repo-1"
        };

        await using var db = _dbFactory.CreateDbContext();
        db.Projects.Add(new ProjectEntity
        {
            Id = projectId,
            Name = project.Name,
            Enabled = project.Enabled,
            Description = project.Description,
            Settings = SerializeToDocument(project)
        });
        db.PipelineJobTemplates.Add(new PipelineJobTemplateEntity
        {
            Id = templateId,
            ProjectId = projectId,
            Name = template.Name,
            Configuration = SerializeToDocument(template)
        });
        await db.SaveChangesAsync();

        var service = new ConfigExportService(_dbFactory);

        // Act
        await service.ExportAsync(_outputDir, CancellationToken.None);

        // Assert
        var projectPath = Path.Combine(_outputDir, "projects", $"{projectId}.json");
        var templatePath = Path.Combine(_outputDir, "projects", projectId.ToString(), "templates", $"{templateId}.json");

        File.Exists(projectPath).Should().BeTrue();
        File.Exists(templatePath).Should().BeTrue();
    }

    [Fact]
    public async Task ExportAsync_ExportsConsolidationRuns()
    {
        // Arrange
        var runId = Guid.NewGuid();
        var run = new ConsolidationRun { RunId = runId.ToString(), Type = ConsolidationRunType.BrainConsolidation, StartedAtUtc = DateTime.UtcNow };

        await using var db = _dbFactory.CreateDbContext();
        db.ConsolidationRuns.Add(new ConsolidationRunEntity
        {
            Id = runId,
            Data = SerializeToDocument(run)
        });
        await db.SaveChangesAsync();

        var service = new ConfigExportService(_dbFactory);

        // Act
        await service.ExportAsync(_outputDir, CancellationToken.None);

        // Assert
        var path = Path.Combine(_outputDir, "consolidation-runs", $"{runId}.json");
        File.Exists(path).Should().BeTrue();
    }

    [Fact]
    public async Task ExportAsync_ExportsPipelineRuns()
    {
        // Arrange
        var runId = Guid.NewGuid();
        await using var db = _dbFactory.CreateDbContext();
        db.PipelineRuns.Add(new PipelineRunEntity
        {
            RunId = runId,
            IssueIdentifier = "TEST-123",
            IssueTitle = "Test Issue",
            FinalStep = PipelineStep.Completed,
            StartedAt = new DateTimeOffset(2025, 1, 15, 10, 0, 0, TimeSpan.Zero),
            CompletedAt = new DateTimeOffset(2025, 1, 15, 10, 30, 0, TimeSpan.Zero),
            RetryCount = 1,
            PullRequestUrl = "https://github.com/test/pr/1",
            ModelName = "gpt-4",
            AgentId = "agent-1",
            ProjectName = "TestProject",
            RunType = PipelineRunType.Implementation
        });
        await db.SaveChangesAsync();

        var service = new ConfigExportService(_dbFactory);

        // Act
        await service.ExportAsync(_outputDir, CancellationToken.None);

        // Assert
        var path = Path.Combine(_outputDir, "runs", $"{runId}.json");
        File.Exists(path).Should().BeTrue();

        var json = await File.ReadAllTextAsync(path);
        var deserialized = JsonSerializer.Deserialize<PipelineRunSummary>(json, PipelineJsonOptions.Default);
        deserialized!.RunId.Should().Be(runId.ToString());
        deserialized.IssueIdentifier.Should().Be("TEST-123");
        deserialized.FinalStep.Should().Be(PipelineStep.Completed);
        deserialized.PullRequestUrl.Should().Be("https://github.com/test/pr/1");
    }

    [Fact]
    public async Task ExportAsync_EmptyDatabase_ProducesNoFiles()
    {
        var service = new ConfigExportService(_dbFactory);

        // Act
        await service.ExportAsync(_outputDir, CancellationToken.None);

        // Assert — only the output dir itself, no subfiles/dirs
        Directory.GetFiles(_outputDir, "*", SearchOption.AllDirectories).Should().BeEmpty();
    }

    [Fact]
    public async Task ExportAsync_ExportsQualityGates()
    {
        // Arrange
        var qgId = Guid.NewGuid();
        var qg = new QualityGateConfiguration { Id = qgId.ToString(), DisplayName = "Build Gate" };

        await using var db = _dbFactory.CreateDbContext();
        db.QualityGateConfigs.Add(new QualityGateConfigEntity
        {
            Id = qgId,
            Name = qg.DisplayName,
            Configuration = SerializeToDocument(qg)
        });
        await db.SaveChangesAsync();

        var service = new ConfigExportService(_dbFactory);

        // Act
        await service.ExportAsync(_outputDir, CancellationToken.None);

        // Assert
        var path = Path.Combine(_outputDir, "quality-gates", $"{qgId}.json");
        File.Exists(path).Should().BeTrue();
    }

    [Fact]
    public async Task ExportAsync_ExportsReviewerConfigs()
    {
        // Arrange
        var reviewerId = Guid.NewGuid();
        var reviewer = new ReviewerConfiguration { Id = reviewerId.ToString(), DisplayName = "Code Reviewer", Agents = [new ReviewAgent { Name = "Agent1", Prompt = "Review code" }] };

        await using var db = _dbFactory.CreateDbContext();
        db.ReviewerConfigs.Add(new ReviewerConfigEntity
        {
            Id = reviewerId,
            Name = reviewer.DisplayName,
            Configuration = SerializeToDocument(reviewer)
        });
        await db.SaveChangesAsync();

        var service = new ConfigExportService(_dbFactory);

        // Act
        await service.ExportAsync(_outputDir, CancellationToken.None);

        // Assert
        var path = Path.Combine(_outputDir, "reviewers", $"{reviewerId}.json");
        File.Exists(path).Should().BeTrue();
    }

    // ── Helpers ─────────────────────────────────────────────────────────

    private async Task SeedPipelineConfig(PipelineConfiguration config)
    {
        await using var db = _dbFactory.CreateDbContext();
        db.PipelineConfig.Add(new PipelineConfigEntity
        {
            Id = Guid.NewGuid(),
            Configuration = SerializeToDocument(config)
        });
        await db.SaveChangesAsync();
    }

    private static string SerializeToDocument<T>(T value)
    {
        return JsonSerializer.Serialize(value, PipelineJsonOptions.Default);
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
    /// Custom PipelineDbContext for InMemory provider — adds JsonDocument value converter
    /// and removes Postgres-specific configs.
    /// </summary>
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
}
