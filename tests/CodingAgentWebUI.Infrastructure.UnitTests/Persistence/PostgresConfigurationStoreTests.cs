using AwesomeAssertions;
using CodingAgentWebUI.Infrastructure.Persistence;
using CodingAgentWebUI.Infrastructure.Persistence.Stores;
using CodingAgentWebUI.Pipeline;
using CodingAgentWebUI.Pipeline.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Xunit;

namespace CodingAgentWebUI.Infrastructure.UnitTests.Persistence;

/// <summary>
/// Unit tests for PostgresConfigurationStore using InMemory EF Core provider.
/// Validates: Requirements 3.6 — existing IConfigurationStore test suite passes against new implementation.
/// </summary>
public class PostgresConfigurationStoreTests : IDisposable
{
    private readonly DbContextOptions<PipelineDbContext> _dbOptions;
    private readonly PostgresConfigurationStore _store;

    public PostgresConfigurationStoreTests()
    {
        var dbName = $"ConfigStoreTests-{Guid.NewGuid()}";
        _dbOptions = new DbContextOptionsBuilder<PipelineDbContext>()
            .UseInMemoryDatabase(dbName)
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;

        // Ensure database is created
        using (var ctx = new InMemoryPipelineDbContext(_dbOptions))
        {
            ctx.Database.EnsureCreated();
        }

        var factory = new InMemoryDbContextFactory(_dbOptions);
        _store = new PostgresConfigurationStore(factory, cacheTtl: TimeSpan.FromMilliseconds(1));
    }

    public void Dispose()
    {
        using var db = new InMemoryPipelineDbContext(_dbOptions);
        db.Database.EnsureDeleted();
    }

    // ── PipelineConfiguration CRUD ─────────────────────────────────────

    [Fact]
    public async Task LoadPipelineConfig_EmptyDb_ReturnsDefaults()
    {
        var config = await _store.LoadPipelineConfigAsync(CancellationToken.None);

        config.Should().NotBeNull();
        config.MaxRetries.Should().Be(3);
        config.AgentTimeout.Should().Be(TimeSpan.FromMinutes(30));
    }

    [Fact]
    public async Task SaveThenLoad_PipelineConfig_RoundTrips()
    {
        var original = new PipelineConfiguration
        {
            MaxRetries = 7,
            AgentTimeout = TimeSpan.FromMinutes(60),
            WorkspaceBaseDirectory = "/custom/path",
            FailedWorkspaceRetentionDays = 21
        };

        await _store.SavePipelineConfigAsync(original, CancellationToken.None);
        // Force cache miss by creating a fresh store
        var freshStore = CreateFreshStore();
        var loaded = await freshStore.LoadPipelineConfigAsync(CancellationToken.None);

        loaded.MaxRetries.Should().Be(7);
        loaded.AgentTimeout.Should().Be(TimeSpan.FromMinutes(60));
        loaded.WorkspaceBaseDirectory.Should().Be("/custom/path");
        loaded.FailedWorkspaceRetentionDays.Should().Be(21);
    }

    [Fact]
    public async Task UpdatePipelineConfig_AppliesTransform()
    {
        var initial = new PipelineConfiguration
        {
            MaxRetries = 2,
            AgentTimeout = TimeSpan.FromMinutes(10)
        };
        await _store.SavePipelineConfigAsync(initial, CancellationToken.None);

        await _store.UpdatePipelineConfigAsync(
            c => c with { MaxRetries = 99 }, CancellationToken.None);

        var freshStore = CreateFreshStore();
        var loaded = await freshStore.LoadPipelineConfigAsync(CancellationToken.None);
        loaded.MaxRetries.Should().Be(99);
        loaded.AgentTimeout.Should().Be(TimeSpan.FromMinutes(10));
    }

    [Fact]
    public async Task UpdatePipelineConfig_NoExistingRow_CreatesFromDefault()
    {
        await _store.UpdatePipelineConfigAsync(
            c => c with { MaxRetries = 42 }, CancellationToken.None);

        var freshStore = CreateFreshStore();
        var loaded = await freshStore.LoadPipelineConfigAsync(CancellationToken.None);
        loaded.MaxRetries.Should().Be(42);
        loaded.AgentTimeout.Should().Be(TimeSpan.FromMinutes(30)); // default
    }

    // ── ProviderConfig CRUD ────────────────────────────────────────────

    [Fact]
    public async Task LoadProviderConfigs_EmptyDb_ReturnsEmpty()
    {
        var configs = await _store.LoadProviderConfigsAsync(
            ProviderKind.Issue, CancellationToken.None);
        configs.Should().BeEmpty();
    }

    [Fact]
    public async Task SaveThenLoad_ProviderConfig_RoundTrips()
    {
        var id = Guid.NewGuid().ToString();
        var original = new ProviderConfig
        {
            Id = id,
            Kind = ProviderKind.Issue,
            ProviderType = "GitHub",
            DisplayName = "My GitHub",
            Settings = new Dictionary<string, string>
            {
                ["apiUrl"] = "https://api.github.com",
                ["owner"] = "testorg"
            }
        };

        await _store.SaveProviderConfigAsync(original, CancellationToken.None);
        var loaded = await _store.LoadProviderConfigsAsync(
            ProviderKind.Issue, CancellationToken.None);

        var match = loaded.Should().ContainSingle().Subject;
        match.Id.Should().Be(id);
        match.Kind.Should().Be(ProviderKind.Issue);
        match.ProviderType.Should().Be("GitHub");
        match.DisplayName.Should().Be("My GitHub");
        match.Settings["apiUrl"].Should().Be("https://api.github.com");
    }

    [Fact]
    public async Task GetProviderConfigById_Exists_ReturnsConfig()
    {
        var id = Guid.NewGuid().ToString();
        var config = new ProviderConfig
        {
            Id = id,
            Kind = ProviderKind.Agent,
            ProviderType = "Kiro",
            DisplayName = "Agent 1"
        };
        await _store.SaveProviderConfigAsync(config, CancellationToken.None);

        var result = await _store.GetProviderConfigByIdAsync(
            id, ProviderKind.Agent, CancellationToken.None);

        result.Should().NotBeNull();
        result!.Id.Should().Be(id);
    }

    [Fact]
    public async Task GetProviderConfigById_NonExistent_ReturnsNull()
    {
        var result = await _store.GetProviderConfigByIdAsync(
            Guid.NewGuid().ToString(), ProviderKind.Issue, CancellationToken.None);
        result.Should().BeNull();
    }

    [Fact]
    public async Task DeleteProviderConfig_RemovesFromDb()
    {
        var id = Guid.NewGuid().ToString();
        var config = new ProviderConfig
        {
            Id = id,
            Kind = ProviderKind.Repository,
            ProviderType = "GitHub",
            DisplayName = "To Delete"
        };
        await _store.SaveProviderConfigAsync(config, CancellationToken.None);
        await _store.DeleteProviderConfigAsync(id, ProviderKind.Repository, CancellationToken.None);

        var loaded = await _store.LoadProviderConfigsAsync(
            ProviderKind.Repository, CancellationToken.None);
        loaded.Should().BeEmpty();
    }

    [Fact]
    public async Task DeleteProviderConfig_NonExistent_DoesNotThrow()
    {
        var act = () => _store.DeleteProviderConfigAsync(
            Guid.NewGuid().ToString(), ProviderKind.Agent, CancellationToken.None);
        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task LoadProviderConfigs_FiltersByKind()
    {
        var issueConfig = new ProviderConfig
        {
            Id = Guid.NewGuid().ToString(),
            Kind = ProviderKind.Issue,
            ProviderType = "GitHub",
            DisplayName = "Issue Provider"
        };
        var agentConfig = new ProviderConfig
        {
            Id = Guid.NewGuid().ToString(),
            Kind = ProviderKind.Agent,
            ProviderType = "Kiro",
            DisplayName = "Agent Provider"
        };
        await _store.SaveProviderConfigAsync(issueConfig, CancellationToken.None);
        await _store.SaveProviderConfigAsync(agentConfig, CancellationToken.None);

        var issueConfigs = await _store.LoadProviderConfigsAsync(
            ProviderKind.Issue, CancellationToken.None);
        var agentConfigs = await _store.LoadProviderConfigsAsync(
            ProviderKind.Agent, CancellationToken.None);

        issueConfigs.Should().ContainSingle().Which.DisplayName.Should().Be("Issue Provider");
        agentConfigs.Should().ContainSingle().Which.DisplayName.Should().Be("Agent Provider");
    }

    // ── AgentProfile CRUD ──────────────────────────────────────────────

    [Fact]
    public async Task LoadAgentProfiles_EmptyDb_ReturnsEmpty()
    {
        var profiles = await _store.LoadAgentProfilesAsync(CancellationToken.None);
        profiles.Should().BeEmpty();
    }

    [Fact]
    public async Task SaveThenLoad_AgentProfile_RoundTrips()
    {
        var id = Guid.NewGuid().ToString();
        var profile = new AgentProfile
        {
            Id = id,
            DisplayName = "Test Profile",
            AgentProviderConfigId = "provider-abc",
            MatchLabels = ["dotnet", "csharp"],
            Enabled = true,
            Priority = 5
        };
        await _store.SaveAgentProfileAsync(profile, CancellationToken.None);

        var loaded = await _store.LoadAgentProfilesAsync(CancellationToken.None);
        var match = loaded.Should().ContainSingle().Subject;
        match.Id.Should().Be(id);
        match.DisplayName.Should().Be("Test Profile");
        match.AgentProviderConfigId.Should().Be("provider-abc");
        match.Priority.Should().Be(5);
    }

    [Fact]
    public async Task DeleteAgentProfile_RemovesFromDb()
    {
        var id = Guid.NewGuid().ToString();
        var profile = new AgentProfile
        {
            Id = id,
            DisplayName = "Delete Me",
            AgentProviderConfigId = "p1",
            Enabled = true,
            Priority = 1
        };
        await _store.SaveAgentProfileAsync(profile, CancellationToken.None);
        await _store.DeleteAgentProfileAsync(id, CancellationToken.None);

        var loaded = await _store.LoadAgentProfilesAsync(CancellationToken.None);
        loaded.Should().BeEmpty();
    }

    [Fact]
    public async Task DeleteAgentProfile_NonExistent_DoesNotThrow()
    {
        var act = () => _store.DeleteAgentProfileAsync(
            Guid.NewGuid().ToString(), CancellationToken.None);
        await act.Should().NotThrowAsync();
    }

    // ── QualityGateConfiguration CRUD ──────────────────────────────────

    [Fact]
    public async Task LoadQualityGateConfigs_EmptyDb_ReturnsEmpty()
    {
        var configs = await _store.LoadQualityGateConfigsAsync(CancellationToken.None);
        configs.Should().BeEmpty();
    }

    [Fact]
    public async Task SaveThenLoad_QualityGateConfig_RoundTrips()
    {
        var id = Guid.NewGuid().ToString();
        var config = new QualityGateConfiguration
        {
            Id = id,
            DisplayName = "Build & Test",
            MatchLabels = ["dotnet"],
            CompilationCommand = "dotnet",
            CompilationArguments = ["build"],
            TestCommand = "dotnet",
            TestArguments = ["test"],
            CoverageThreshold = 80.0,
            Enabled = true
        };
        await _store.SaveQualityGateConfigAsync(config, CancellationToken.None);

        var loaded = await _store.LoadQualityGateConfigsAsync(CancellationToken.None);
        var match = loaded.Should().ContainSingle().Subject;
        match.Id.Should().Be(id);
        match.DisplayName.Should().Be("Build & Test");
        match.CompilationCommand.Should().Be("dotnet");
        match.CoverageThreshold.Should().Be(80.0);
    }

    [Fact]
    public async Task DeleteQualityGateConfig_RemovesFromDb()
    {
        var id = Guid.NewGuid().ToString();
        var config = new QualityGateConfiguration
        {
            Id = id,
            DisplayName = "To Delete",
            Enabled = true
        };
        await _store.SaveQualityGateConfigAsync(config, CancellationToken.None);
        await _store.DeleteQualityGateConfigAsync(id, CancellationToken.None);

        var loaded = await _store.LoadQualityGateConfigsAsync(CancellationToken.None);
        loaded.Should().BeEmpty();
    }

    // ── ReviewerConfiguration CRUD ─────────────────────────────────────

    [Fact]
    public async Task LoadReviewerConfigs_EmptyDb_ReturnsEmpty()
    {
        var configs = await _store.LoadReviewerConfigsAsync(CancellationToken.None);
        configs.Should().BeEmpty();
    }

    [Fact]
    public async Task SaveThenLoad_ReviewerConfig_RoundTrips()
    {
        var id = Guid.NewGuid().ToString();
        var config = new ReviewerConfiguration
        {
            Id = id,
            DisplayName = "Security Review",
            MatchLabels = ["security"],
            Agents = [new ReviewAgent { Name = "SecurityBot", Prompt = "Review for vulns" }],
            Enabled = true,
            ExecutionOrder = 1
        };
        await _store.SaveReviewerConfigAsync(config, CancellationToken.None);

        var loaded = await _store.LoadReviewerConfigsAsync(CancellationToken.None);
        var match = loaded.Should().ContainSingle().Subject;
        match.Id.Should().Be(id);
        match.DisplayName.Should().Be("Security Review");
        match.Agents.Should().ContainSingle().Which.Name.Should().Be("SecurityBot");
    }

    [Fact]
    public async Task DeleteReviewerConfig_RemovesFromDb()
    {
        var id = Guid.NewGuid().ToString();
        var config = new ReviewerConfiguration
        {
            Id = id,
            DisplayName = "Delete Me",
            Agents = [new ReviewAgent { Name = "Bot", Prompt = "p" }]
        };
        await _store.SaveReviewerConfigAsync(config, CancellationToken.None);
        await _store.DeleteReviewerConfigAsync(id, CancellationToken.None);

        var loaded = await _store.LoadReviewerConfigsAsync(CancellationToken.None);
        loaded.Should().BeEmpty();
    }

    // ── Project CRUD ───────────────────────────────────────────────────

    [Fact]
    public async Task LoadProjects_EmptyDb_ReturnsEmpty()
    {
        var projects = await _store.LoadProjectsAsync(CancellationToken.None);
        projects.Should().BeEmpty();
    }

    [Fact]
    public async Task SaveThenLoad_Project_RoundTrips()
    {
        var id = Guid.NewGuid().ToString();
        var project = new PipelineProject
        {
            Id = id,
            Name = "MyProject",
            Description = "A test project",
            Enabled = true,
            TemplateIds = ["t1", "t2"]
        };
        await _store.SaveProjectAsync(project, CancellationToken.None);

        var loaded = await _store.LoadProjectsAsync(CancellationToken.None);
        var match = loaded.Should().ContainSingle().Subject;
        match.Id.Should().Be(id);
        match.Name.Should().Be("MyProject");
        match.Description.Should().Be("A test project");
        match.Enabled.Should().BeTrue();
    }

    [Fact]
    public async Task GetProjectById_Exists_ReturnsProject()
    {
        var id = Guid.NewGuid().ToString();
        var project = new PipelineProject
        {
            Id = id,
            Name = "FindMe",
            Enabled = true,
            TemplateIds = []
        };
        await _store.SaveProjectAsync(project, CancellationToken.None);

        var result = await _store.GetProjectByIdAsync(id, CancellationToken.None);
        result.Should().NotBeNull();
        result!.Name.Should().Be("FindMe");
    }

    [Fact]
    public async Task GetProjectById_NonExistent_ReturnsNull()
    {
        var result = await _store.GetProjectByIdAsync(
            Guid.NewGuid().ToString(), CancellationToken.None);
        result.Should().BeNull();
    }

    [Fact]
    public async Task DeleteProject_RemovesFromDb()
    {
        var id = Guid.NewGuid().ToString();
        var project = new PipelineProject
        {
            Id = id,
            Name = "Delete Me",
            Enabled = true,
            TemplateIds = []
        };
        await _store.SaveProjectAsync(project, CancellationToken.None);
        await _store.DeleteProjectAsync(id, CancellationToken.None);

        var loaded = await _store.LoadProjectsAsync(CancellationToken.None);
        loaded.Should().BeEmpty();
    }

    // ── Template CRUD ──────────────────────────────────────────────────

    [Fact]
    public async Task SaveThenLoad_Template_RoundTrips()
    {
        var projectId = Guid.NewGuid().ToString();
        var project = new PipelineProject
        {
            Id = projectId,
            Name = "TemplateProject",
            Enabled = true,
            TemplateIds = []
        };
        await _store.SaveProjectAsync(project, CancellationToken.None);

        var templateId = Guid.NewGuid().ToString();
        var template = new PipelineJobTemplate
        {
            Id = templateId,
            Name = "Build Template",
            IssueProviderId = "issue-1",
            RepoProviderId = "repo-1"
        };
        await _store.SaveTemplateAsync(projectId, template, CancellationToken.None);

        var loaded = await _store.LoadTemplatesForProjectAsync(
            projectId, CancellationToken.None);
        var match = loaded.Should().ContainSingle().Subject;
        match.Id.Should().Be(templateId);
        match.Name.Should().Be("Build Template");
    }

    [Fact]
    public async Task LoadAllTemplates_ReturnsAllAcrossProjects()
    {
        var p1 = Guid.NewGuid().ToString();
        var p2 = Guid.NewGuid().ToString();
        await _store.SaveProjectAsync(new PipelineProject
            { Id = p1, Name = "P1", Enabled = true, TemplateIds = [] }, CancellationToken.None);
        await _store.SaveProjectAsync(new PipelineProject
            { Id = p2, Name = "P2", Enabled = true, TemplateIds = [] }, CancellationToken.None);

        var t1 = new PipelineJobTemplate
            { Id = Guid.NewGuid().ToString(), Name = "T1", IssueProviderId = "i1", RepoProviderId = "r1" };
        var t2 = new PipelineJobTemplate
            { Id = Guid.NewGuid().ToString(), Name = "T2", IssueProviderId = "i2", RepoProviderId = "r2" };
        await _store.SaveTemplateAsync(p1, t1, CancellationToken.None);
        await _store.SaveTemplateAsync(p2, t2, CancellationToken.None);

        var all = await _store.LoadAllTemplatesAsync(CancellationToken.None);
        all.Should().HaveCount(2);
    }

    [Fact]
    public async Task DeleteTemplate_RemovesFromProject()
    {
        var projectId = Guid.NewGuid().ToString();
        await _store.SaveProjectAsync(new PipelineProject
            { Id = projectId, Name = "P", Enabled = true, TemplateIds = [] }, CancellationToken.None);

        var templateId = Guid.NewGuid().ToString();
        var template = new PipelineJobTemplate
            { Id = templateId, Name = "T", IssueProviderId = "i", RepoProviderId = "r" };
        await _store.SaveTemplateAsync(projectId, template, CancellationToken.None);
        await _store.DeleteTemplateAsync(projectId, templateId, CancellationToken.None);

        var loaded = await _store.LoadTemplatesForProjectAsync(
            projectId, CancellationToken.None);
        loaded.Should().BeEmpty();
    }

    // ── Cache Invalidation ─────────────────────────────────────────────

    [Fact]
    public async Task PipelineConfig_CacheInvalidatedOnSave_ImmediateReadReturnsFresh()
    {
        // Initial load caches default
        var initial = await _store.LoadPipelineConfigAsync(CancellationToken.None);
        initial.MaxRetries.Should().Be(3);

        // Save different config
        await _store.SavePipelineConfigAsync(
            new PipelineConfiguration { MaxRetries = 55 }, CancellationToken.None);

        // Immediate read from same store should return fresh data (cache invalidated)
        var reloaded = await _store.LoadPipelineConfigAsync(CancellationToken.None);
        reloaded.MaxRetries.Should().Be(55);
    }

    [Fact]
    public async Task ProviderConfig_CacheInvalidatedOnSave()
    {
        // Initial load returns empty (caches empty list)
        var empty = await _store.LoadProviderConfigsAsync(
            ProviderKind.Issue, CancellationToken.None);
        empty.Should().BeEmpty();

        // Save a config (invalidates cache for that kind)
        await _store.SaveProviderConfigAsync(new ProviderConfig
        {
            Id = Guid.NewGuid().ToString(),
            Kind = ProviderKind.Issue,
            ProviderType = "GitHub",
            DisplayName = "New"
        }, CancellationToken.None);

        // Immediate read reflects the new config
        var reloaded = await _store.LoadProviderConfigsAsync(
            ProviderKind.Issue, CancellationToken.None);
        reloaded.Should().ContainSingle();
    }

    [Fact]
    public async Task AgentProfile_CacheInvalidatedOnSave()
    {
        var empty = await _store.LoadAgentProfilesAsync(CancellationToken.None);
        empty.Should().BeEmpty();

        await _store.SaveAgentProfileAsync(new AgentProfile
        {
            Id = Guid.NewGuid().ToString(),
            DisplayName = "P1",
            AgentProviderConfigId = "x",
            Enabled = true,
            Priority = 1
        }, CancellationToken.None);

        var reloaded = await _store.LoadAgentProfilesAsync(CancellationToken.None);
        reloaded.Should().ContainSingle();
    }

    // ── Non-existent ID lookups ────────────────────────────────────────

    [Fact]
    public async Task GetProviderConfigById_InvalidGuid_ReturnsNull()
    {
        var result = await _store.GetProviderConfigByIdAsync(
            "not-a-guid", ProviderKind.Issue, CancellationToken.None);
        result.Should().BeNull();
    }

    [Fact]
    public async Task GetProjectById_InvalidGuid_ReturnsNull()
    {
        var result = await _store.GetProjectByIdAsync(
            "not-a-guid", CancellationToken.None);
        result.Should().BeNull();
    }

    [Fact]
    public async Task LoadTemplatesForProject_NonExistentProject_ReturnsEmpty()
    {
        var result = await _store.LoadTemplatesForProjectAsync(
            Guid.NewGuid().ToString(), CancellationToken.None);
        result.Should().BeEmpty();
    }

    // ── Save updates existing (upsert behavior) ────────────────────────

    [Fact]
    public async Task SaveProviderConfig_ExistingId_Updates()
    {
        var id = Guid.NewGuid().ToString();
        var original = new ProviderConfig
        {
            Id = id,
            Kind = ProviderKind.Issue,
            ProviderType = "GitHub",
            DisplayName = "Original"
        };
        await _store.SaveProviderConfigAsync(original, CancellationToken.None);

        var updated = new ProviderConfig
        {
            Id = id,
            Kind = ProviderKind.Issue,
            ProviderType = "GitHub",
            DisplayName = "Updated"
        };
        await _store.SaveProviderConfigAsync(updated, CancellationToken.None);

        var loaded = await _store.LoadProviderConfigsAsync(
            ProviderKind.Issue, CancellationToken.None);
        loaded.Should().ContainSingle().Which.DisplayName.Should().Be("Updated");
    }

    [Fact]
    public async Task SaveAgentProfile_ExistingId_Updates()
    {
        var id = Guid.NewGuid().ToString();
        await _store.SaveAgentProfileAsync(new AgentProfile
        {
            Id = id, DisplayName = "V1", AgentProviderConfigId = "x",
            Enabled = true, Priority = 1
        }, CancellationToken.None);

        await _store.SaveAgentProfileAsync(new AgentProfile
        {
            Id = id, DisplayName = "V2", AgentProviderConfigId = "x",
            Enabled = true, Priority = 2
        }, CancellationToken.None);

        var loaded = await _store.LoadAgentProfilesAsync(CancellationToken.None);
        var match = loaded.Should().ContainSingle().Subject;
        match.DisplayName.Should().Be("V2");
        match.Priority.Should().Be(2);
    }

    // ── Helper: InMemoryDbContextFactory ────────────────────────────────

    private PostgresConfigurationStore CreateFreshStore()
    {
        var factory = new InMemoryDbContextFactory(_dbOptions);
        return new PostgresConfigurationStore(factory, cacheTtl: TimeSpan.FromMilliseconds(1));
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

            // Remove RowVersion concurrency token config for InMemory
            foreach (var entityType in modelBuilder.Model.GetEntityTypes())
            {
                var rowVersionProp = entityType.FindProperty("RowVersion");
                if (rowVersionProp != null)
                {
                    rowVersionProp.IsConcurrencyToken = false;
                    rowVersionProp.ValueGenerated = Microsoft.EntityFrameworkCore.Metadata.ValueGenerated.Never;
                }
            }

            // Remove filter-based unique indexes (not supported by InMemory)
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

    /// <summary>
    /// IDbContextFactory implementation backed by InMemory provider for unit testing.
    /// Returns InMemoryPipelineDbContext which has JsonDocument value converters.
    /// </summary>
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
