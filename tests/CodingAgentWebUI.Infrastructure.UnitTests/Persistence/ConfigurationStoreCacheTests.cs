using AwesomeAssertions;
using CodingAgentWebUI.Infrastructure.Persistence;
using CodingAgentWebUI.Pipeline.Models;
using Xunit;

namespace CodingAgentWebUI.Infrastructure.UnitTests.Persistence;

public class ConfigurationStoreCacheTests : IDisposable
{
    private readonly string _tempDir;
    private readonly JsonConfigurationStore _store;

    public ConfigurationStoreCacheTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"cache-tests-{Guid.NewGuid()}");
        Directory.CreateDirectory(_tempDir);
        _store = new JsonConfigurationStore(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    // ── PipelineConfiguration cache ───────────────────────────────────────

    [Fact]
    public async Task LoadPipelineConfig_SecondCall_ReturnsCachedInstance()
    {
        var first = await _store.LoadPipelineConfigAsync(CancellationToken.None);
        var second = await _store.LoadPipelineConfigAsync(CancellationToken.None);

        ReferenceEquals(first, second).Should().BeTrue();
    }

    [Fact]
    public async Task SavePipelineConfig_InvalidatesCache_NextLoadReadsFreshData()
    {
        // Load to populate cache
        var original = await _store.LoadPipelineConfigAsync(CancellationToken.None);
        original.MaxRetries.Should().Be(3); // default

        // Save different config — invalidates cache
        var updated = new PipelineConfiguration
        {
            MaxRetries = 99,
            AgentTimeout = TimeSpan.FromMinutes(5),
            WorkspaceBaseDirectory = "/fresh"
        };
        await _store.SavePipelineConfigAsync(updated, CancellationToken.None);

        // Next load should read fresh data from disk
        var reloaded = await _store.LoadPipelineConfigAsync(CancellationToken.None);
        reloaded.MaxRetries.Should().Be(99);
        reloaded.WorkspaceBaseDirectory.Should().Be("/fresh");
    }

    // ── ProviderConfig cache ──────────────────────────────────────────────

    [Fact]
    public async Task LoadProviderConfigs_SecondCall_ReturnsCachedInstance()
    {
        // Seed a provider so the list is non-empty
        var config = new ProviderConfig
        {
            Id = "cached-provider",
            Kind = ProviderKind.Issue,
            ProviderType = "GitHub",
            DisplayName = "Cached"
        };
        await _store.SaveProviderConfigAsync(config, CancellationToken.None);

        var first = await _store.LoadProviderConfigsAsync(ProviderKind.Issue, CancellationToken.None);
        var second = await _store.LoadProviderConfigsAsync(ProviderKind.Issue, CancellationToken.None);

        ReferenceEquals(first, second).Should().BeTrue();
    }

    [Fact]
    public async Task SaveProviderConfig_InvalidatesCache_NextLoadReflectsChange()
    {
        // Initial load
        var initial = await _store.LoadProviderConfigsAsync(ProviderKind.Agent, CancellationToken.None);
        initial.Should().BeEmpty();

        // Save a new provider config
        var config = new ProviderConfig
        {
            Id = "new-agent-provider",
            Kind = ProviderKind.Agent,
            ProviderType = "Kiro",
            DisplayName = "New Agent"
        };
        await _store.SaveProviderConfigAsync(config, CancellationToken.None);

        // Next load should contain the new config
        var reloaded = await _store.LoadProviderConfigsAsync(ProviderKind.Agent, CancellationToken.None);
        reloaded.Should().ContainSingle(c => c.Id == "new-agent-provider");
    }

    [Fact]
    public async Task DeleteProviderConfig_InvalidatesCache()
    {
        // Save then load to populate cache
        var config = new ProviderConfig
        {
            Id = "delete-me",
            Kind = ProviderKind.Repository,
            ProviderType = "GitHub",
            DisplayName = "To Delete"
        };
        await _store.SaveProviderConfigAsync(config, CancellationToken.None);
        var before = await _store.LoadProviderConfigsAsync(ProviderKind.Repository, CancellationToken.None);
        before.Should().ContainSingle(c => c.Id == "delete-me");

        // Delete — invalidates cache
        await _store.DeleteProviderConfigAsync("delete-me", ProviderKind.Repository, CancellationToken.None);

        // Next load should not contain the deleted config
        var after = await _store.LoadProviderConfigsAsync(ProviderKind.Repository, CancellationToken.None);
        after.Should().NotContain(c => c.Id == "delete-me");
    }

    // ── Projects cache ────────────────────────────────────────────────────

    [Fact]
    public async Task LoadProjects_SecondCall_ReturnsCachedInstance()
    {
        var first = await _store.LoadProjectsAsync(CancellationToken.None);
        var second = await _store.LoadProjectsAsync(CancellationToken.None);

        ReferenceEquals(first, second).Should().BeTrue();
    }

    [Fact]
    public async Task SaveProject_InvalidatesCache()
    {
        // Initial load (contains Default project from constructor)
        var initial = await _store.LoadProjectsAsync(CancellationToken.None);
        var initialCount = initial.Count;

        // Save a new project
        var project = new PipelineProject
        {
            Id = Guid.NewGuid().ToString(),
            Name = "CacheTestProject",
            Enabled = true,
            TemplateIds = []
        };
        await _store.SaveProjectAsync(project, CancellationToken.None);

        // Next load should contain the new project
        var reloaded = await _store.LoadProjectsAsync(CancellationToken.None);
        reloaded.Count.Should().Be(initialCount + 1);
        reloaded.Should().Contain(p => p.Name == "CacheTestProject");
    }

    // ── Templates cache ───────────────────────────────────────────────────

    [Fact]
    public async Task LoadAllTemplates_SecondCall_ReturnsCachedInstance()
    {
        var first = await _store.LoadAllTemplatesAsync(CancellationToken.None);
        var second = await _store.LoadAllTemplatesAsync(CancellationToken.None);

        ReferenceEquals(first, second).Should().BeTrue();
    }

    [Fact]
    public async Task SaveTemplate_InvalidatesTemplateAndProjectCache()
    {
        // Use the Default project (always exists)
        var projects = await _store.LoadProjectsAsync(CancellationToken.None);
        var defaultProject = projects.First(p => p.Id == WellKnownIds.DefaultProjectId);

        var template = new PipelineJobTemplate
        {
            Id = Guid.NewGuid().ToString(),
            Name = "CacheTestTemplate",
            IssueProviderId = "issue-1",
            RepoProviderId = "repo-1"
        };
        await _store.SaveTemplateAsync(defaultProject.Id, template, CancellationToken.None);

        // LoadAllTemplates should reflect the new template
        var allTemplates = await _store.LoadAllTemplatesAsync(CancellationToken.None);
        allTemplates.Should().Contain(t => t.Name == "CacheTestTemplate");
    }

    // ── AgentProfiles cache ───────────────────────────────────────────────

    [Fact]
    public async Task LoadAgentProfiles_CacheInvalidatedOnSave()
    {
        // Initial load
        var initial = await _store.LoadAgentProfilesAsync(CancellationToken.None);
        initial.Should().BeEmpty();

        // Save a profile
        var profile = new AgentProfile
        {
            Id = "cache-profile-1",
            DisplayName = "Cache Test Profile",
            AgentProviderConfigId = "provider-abc",
            Enabled = true,
            Priority = 1
        };
        await _store.SaveAgentProfileAsync(profile, CancellationToken.None);

        // Next load should return updated list containing the new profile
        var reloaded = await _store.LoadAgentProfilesAsync(CancellationToken.None);
        reloaded.Should().ContainSingle(p => p.Id == "cache-profile-1");
    }
}
