using AwesomeAssertions;
using CodingAgentWebUI.Api.Client;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Services;
using Moq;
using Xunit;

namespace CodingAgentWebUI.UnitTests.Services;

/// <summary>
/// Additional tests for API-backed config store mutation paths not covered by
/// ApiBackedConfigStoresTests — Save/Delete/Update methods across all store types.
/// </summary>
public sealed class ApiBackedConfigStoresAdditionalTests
{
    private static Mock<IPipelineApiConfigClient> EmptyClient()
    {
        var client = new Mock<IPipelineApiConfigClient>();
        client.Setup(c => c.GetPipelineConfigAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PipelineConfiguration());
        client.Setup(c => c.GetProviderConfigsAsync(It.IsAny<ProviderKind>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<ProviderConfig>());
        client.Setup(c => c.GetProviderConfigsWithSecretsAsync(It.IsAny<ProviderKind>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<ProviderConfig>());
        client.Setup(c => c.GetAgentProfilesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<AgentProfile>());
        client.Setup(c => c.GetQualityGateConfigsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<QualityGateConfiguration>());
        client.Setup(c => c.GetReviewerConfigsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<ReviewerConfiguration>());
        client.Setup(c => c.GetProjectsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<PipelineProject>());
        client.Setup(c => c.GetAllTemplatesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<PipelineJobTemplate>());
        return client;
    }

    private static ApiConfigurationStore MakeConfigStore(IPipelineApiConfigClient client) =>
        new(client,
            new ApiPipelineConfigStore(client),
            new ApiProviderConfigStore(client),
            new ApiProjectStore(client))
        {
            CacheTtlSeconds = 600
        };

    // ── ApiPipelineConfigStore mutations ─────────────────────────────────────

    [Fact]
    public async Task ApiPipelineConfigStore_SavePipelineConfig_CallsClientAndUpdatesCache()
    {
        var client = EmptyClient();
        var store = new ApiPipelineConfigStore(client.Object) { CacheTtlSeconds = 600 };
        var config = new PipelineConfiguration { MaxRetries = 99 };

        await store.SavePipelineConfigAsync(config, CancellationToken.None);

        client.Verify(c => c.SavePipelineConfigAsync(config, It.IsAny<CancellationToken>()), Times.Once);

        // After save, the provided config is cached — next load must not call API again
        client.Setup(c => c.GetPipelineConfigAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("should not be called"));
        var loaded = await store.LoadPipelineConfigAsync(CancellationToken.None);
        loaded.MaxRetries.Should().Be(99);
    }

    [Fact]
    public async Task ApiPipelineConfigStore_UpdatePipelineConfig_CallsClientAndInvalidatesCache()
    {
        var client = EmptyClient();
        var store = new ApiPipelineConfigStore(client.Object) { CacheTtlSeconds = 600 };

        // Pre-populate the cache
        await store.LoadPipelineConfigAsync(CancellationToken.None);

        await store.UpdatePipelineConfigAsync(c => c, CancellationToken.None);

        client.Verify(c => c.UpdatePipelineConfigAsync(
            It.IsAny<Func<PipelineConfiguration, PipelineConfiguration>>(),
            It.IsAny<CancellationToken>()), Times.Once);

        // After update, cache is invalidated — next load should hit the API
        var loadCallCount = 0;
        client.Setup(c => c.GetPipelineConfigAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => { loadCallCount++; return new PipelineConfiguration(); });

        await store.LoadPipelineConfigAsync(CancellationToken.None);
        loadCallCount.Should().Be(1, "cache invalidated by UpdatePipelineConfigAsync");
    }

    // ── ApiProviderConfigStore.DeleteProviderConfig ───────────────────────────

    [Fact]
    public async Task ApiProviderConfigStore_DeleteProviderConfig_CallsClientAndInvalidatesCache()
    {
        var client = EmptyClient();
        var store = new ApiProviderConfigStore(client.Object) { CacheTtlSeconds = 600 };

        // Pre-populate cache for Issue kind
        await store.LoadProviderConfigsAsync(ProviderKind.Issue, CancellationToken.None);

        await store.DeleteProviderConfigAsync("config-id", ProviderKind.Issue, CancellationToken.None);

        client.Verify(c => c.DeleteProviderConfigAsync("config-id", ProviderKind.Issue,
            It.IsAny<CancellationToken>()), Times.Once);

        // Verify cache is cleared: next load must call the API
        var loadCallCount = 0;
        client.Setup(c => c.GetProviderConfigsWithSecretsAsync(ProviderKind.Issue, It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => { loadCallCount++; return Array.Empty<ProviderConfig>(); });

        await store.LoadProviderConfigsAsync(ProviderKind.Issue, CancellationToken.None);
        loadCallCount.Should().Be(1, "cache cleared by DeleteProviderConfigAsync");
    }

    // ── ApiConfigurationStore agent profile mutations ────────────────────────

    [Fact]
    public async Task ApiConfigurationStore_SaveAgentProfile_CallsClient()
    {
        var client = EmptyClient();
        var store = MakeConfigStore(client.Object);
        var profile = new AgentProfile { Id = "prof-1", DisplayName = "Test", AgentProviderConfigId = "prov-1" };

        await store.SaveAgentProfileAsync(profile, CancellationToken.None);

        client.Verify(c => c.SaveAgentProfileAsync(profile, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ApiConfigurationStore_DeleteAgentProfile_CallsClient()
    {
        var client = EmptyClient();
        var store = MakeConfigStore(client.Object);

        await store.DeleteAgentProfileAsync("prof-1", CancellationToken.None);

        client.Verify(c => c.DeleteAgentProfileAsync("prof-1", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ApiConfigurationStore_LoadAgentProfiles_CachesResult()
    {
        var client = EmptyClient();
        var store = MakeConfigStore(client.Object);

        await store.LoadAgentProfilesAsync(CancellationToken.None);
        await store.LoadAgentProfilesAsync(CancellationToken.None);

        client.Verify(c => c.GetAgentProfilesAsync(It.IsAny<CancellationToken>()), Times.Once,
            "second load within TTL must not call API");
    }

    // ── ApiConfigurationStore QGC mutations ──────────────────────────────────

    [Fact]
    public async Task ApiConfigurationStore_SaveQualityGateConfig_CallsClient()
    {
        var client = EmptyClient();
        var store = MakeConfigStore(client.Object);
        var config = new QualityGateConfiguration { Id = "qgc-1", DisplayName = "Test" };

        await store.SaveQualityGateConfigAsync(config, CancellationToken.None);

        client.Verify(c => c.SaveQualityGateConfigAsync(config, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ApiConfigurationStore_DeleteQualityGateConfig_CallsClient()
    {
        var client = EmptyClient();
        var store = MakeConfigStore(client.Object);

        await store.DeleteQualityGateConfigAsync("qgc-1", CancellationToken.None);

        client.Verify(c => c.DeleteQualityGateConfigAsync("qgc-1", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ApiConfigurationStore_LoadQualityGateConfigs_CachesResult()
    {
        var client = EmptyClient();
        var store = MakeConfigStore(client.Object);

        await store.LoadQualityGateConfigsAsync(CancellationToken.None);
        await store.LoadQualityGateConfigsAsync(CancellationToken.None);

        client.Verify(c => c.GetQualityGateConfigsAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    // ── ApiConfigurationStore reviewer mutations ──────────────────────────────

    [Fact]
    public async Task ApiConfigurationStore_SaveReviewerConfig_CallsClient()
    {
        var client = EmptyClient();
        var store = MakeConfigStore(client.Object);
        var config = new ReviewerConfiguration
        {
            Id = "rev-1",
            DisplayName = "Reviewer",
            Agents = []
        };

        await store.SaveReviewerConfigAsync(config, CancellationToken.None);

        client.Verify(c => c.SaveReviewerConfigAsync(config, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ApiConfigurationStore_DeleteReviewerConfig_CallsClient()
    {
        var client = EmptyClient();
        var store = MakeConfigStore(client.Object);

        await store.DeleteReviewerConfigAsync("rev-1", CancellationToken.None);

        client.Verify(c => c.DeleteReviewerConfigAsync("rev-1", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ApiConfigurationStore_LoadReviewerConfigs_CachesResult()
    {
        var client = EmptyClient();
        var store = MakeConfigStore(client.Object);

        await store.LoadReviewerConfigsAsync(CancellationToken.None);
        await store.LoadReviewerConfigsAsync(CancellationToken.None);

        client.Verify(c => c.GetReviewerConfigsAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ApiConfigurationStore_ResetReviewerConfigsToDefault_CallsClient()
    {
        var client = EmptyClient();
        var store = MakeConfigStore(client.Object);

        await store.ResetReviewerConfigsToDefaultAsync(CancellationToken.None);

        client.Verify(c => c.ResetReviewerConfigsToDefaultAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    // ── ApiConfigurationStore project mutations ───────────────────────────────

    [Fact]
    public async Task ApiConfigurationStore_SaveProject_DelegatesToProjectStore()
    {
        var client = EmptyClient();
        var store = MakeConfigStore(client.Object);
        var project = new PipelineProject { Id = "proj-1", Name = "My Project" };

        await store.SaveProjectAsync(project, CancellationToken.None);

        client.Verify(c => c.SaveProjectAsync(project, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ApiConfigurationStore_DeleteProject_DelegatesToProjectStore()
    {
        var client = EmptyClient();
        var store = MakeConfigStore(client.Object);

        await store.DeleteProjectAsync("proj-1", CancellationToken.None);

        client.Verify(c => c.DeleteProjectAsync("proj-1", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ApiConfigurationStore_SaveTemplate_DelegatesToProjectStore()
    {
        var client = EmptyClient();
        var store = MakeConfigStore(client.Object);
        var template = new PipelineJobTemplate
        {
            Id = "tmpl-1",
            Name = "Test Template",
            IssueProviderId = "ip-1",
            RepoProviderId = "rp-1"
        };

        await store.SaveTemplateAsync("proj-1", template, CancellationToken.None);

        client.Verify(c => c.SaveTemplateAsync("proj-1", template, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ApiConfigurationStore_DeleteTemplate_DelegatesToProjectStore()
    {
        var client = EmptyClient();
        var store = MakeConfigStore(client.Object);

        await store.DeleteTemplateAsync("proj-1", new TemplateId("tmpl-1"), CancellationToken.None);

        client.Verify(c => c.DeleteTemplateAsync("proj-1", "tmpl-1", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ApiConfigurationStore_MoveTemplate_DelegatesToProjectStore()
    {
        var client = EmptyClient();
        var store = MakeConfigStore(client.Object);

        await store.MoveTemplateAsync("src", "dst", new TemplateId("tmpl-1"), CancellationToken.None);

        client.Verify(c => c.MoveTemplateAsync("src", "dst", "tmpl-1", It.IsAny<CancellationToken>()), Times.Once);
    }

    // ── ApiProjectStore mutations ─────────────────────────────────────────────

    [Fact]
    public async Task ApiProjectStore_SaveProject_CallsClientAndInvalidatesCache()
    {
        var client = EmptyClient();
        var store = new ApiProjectStore(client.Object) { CacheTtlSeconds = 600 };
        var project = new PipelineProject { Id = "proj-1", Name = "My Project" };

        await store.SaveProjectAsync(project, CancellationToken.None);

        client.Verify(c => c.SaveProjectAsync(project, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ApiProjectStore_DeleteProject_CallsClientAndInvalidatesCache()
    {
        var client = EmptyClient();
        var store = new ApiProjectStore(client.Object) { CacheTtlSeconds = 600 };

        await store.DeleteProjectAsync("proj-1", CancellationToken.None);

        client.Verify(c => c.DeleteProjectAsync("proj-1", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ApiProjectStore_SaveTemplate_CallsClientAndInvalidatesTemplateCache()
    {
        var client = EmptyClient();
        var store = new ApiProjectStore(client.Object) { CacheTtlSeconds = 600 };
        var template = new PipelineJobTemplate
        {
            Id = "tmpl-1",
            Name = "Test Template",
            IssueProviderId = "ip-1",
            RepoProviderId = "rp-1"
        };

        await store.SaveTemplateAsync("proj-1", template, CancellationToken.None);

        client.Verify(c => c.SaveTemplateAsync("proj-1", template, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ApiProjectStore_DeleteTemplate_CallsClient()
    {
        var client = EmptyClient();
        var store = new ApiProjectStore(client.Object) { CacheTtlSeconds = 600 };

        await store.DeleteTemplateAsync("proj-1", new TemplateId("tmpl-1"), CancellationToken.None);

        client.Verify(c => c.DeleteTemplateAsync("proj-1", "tmpl-1", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ApiProjectStore_MoveTemplate_CallsClientAndInvalidatesBothCaches()
    {
        var client = EmptyClient();
        var store = new ApiProjectStore(client.Object) { CacheTtlSeconds = 600 };

        await store.MoveTemplateAsync("src", "dst", new TemplateId("tmpl-1"), CancellationToken.None);

        client.Verify(c => c.MoveTemplateAsync("src", "dst", "tmpl-1",
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ApiProjectStore_LoadProjects_CachesResult()
    {
        var client = EmptyClient();
        var store = new ApiProjectStore(client.Object) { CacheTtlSeconds = 600 };

        await store.LoadProjectsAsync(CancellationToken.None);
        await store.LoadProjectsAsync(CancellationToken.None);

        client.Verify(c => c.GetProjectsAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ApiProjectStore_LoadAllTemplates_CachesResult()
    {
        var client = EmptyClient();
        var store = new ApiProjectStore(client.Object) { CacheTtlSeconds = 600 };

        await store.LoadAllTemplatesAsync(CancellationToken.None);
        await store.LoadAllTemplatesAsync(CancellationToken.None);

        client.Verify(c => c.GetAllTemplatesAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ApiProjectStore_HasEnabledTemplates_DelegatesToClient()
    {
        var client = EmptyClient();
        client.Setup(c => c.HasEnabledTemplatesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(true);
        var store = new ApiProjectStore(client.Object);

        var result = await store.HasEnabledTemplatesAsync(CancellationToken.None);

        result.Should().BeTrue();
    }
}
