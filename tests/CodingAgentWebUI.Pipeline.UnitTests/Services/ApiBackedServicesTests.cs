using System.Net;
using System.Net.Http;
using AwesomeAssertions;
using CodingAgentWebUI.Orchestration.Dispatch;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Services;
using CodingAgentWebUI.Api.Client.Stores;
using ApiBackedPipelineRunHistoryService = CodingAgentWebUI.Api.Client.Stores.ApiBackedPipelineRunHistoryService;
using Moq;
using ILogger = Serilog.ILogger;

namespace CodingAgentWebUI.Pipeline.UnitTests.Services;

/// <summary>
/// Tests for API-backed service adapters:
/// ApiPipelineConfigStore, ApiProviderConfigStore, ProviderConfigCache, ApiProjectStore,
/// ApiConfigurationStore, ApiBackedConsolidationRunStore, ApiBackedHarnessSuggestionStore,
/// ApiBackedPendingWorkQuery, ApiBackedPipelineRunHistoryService,
/// ApiBackedWorkItemFallbackTransitionService, ApiChatJobDispatcher.
/// </summary>
public sealed class ApiBackedServicesTests
{
    // ─────────────────────────────────────────────────────────────────────
    // ApiPipelineConfigStore
    // ─────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task PipelineConfigStore_LoadPipelineConfigAsync_CallsClientOnFirstLoad()
    {
        var mockClient = new Mock<CodingAgentWebUI.Api.Client.IPipelineApiConfigClient>();
        var expected = new PipelineConfiguration();
        mockClient.Setup(c => c.GetPipelineConfigAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(expected);

        var store = new ApiPipelineConfigStore(mockClient.Object);
        var result = await store.LoadPipelineConfigAsync(CancellationToken.None);

        result.Should().BeSameAs(expected);
        mockClient.Verify(c => c.GetPipelineConfigAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task PipelineConfigStore_LoadPipelineConfigAsync_ReturnsCachedValueWithinTtl()
    {
        var mockClient = new Mock<CodingAgentWebUI.Api.Client.IPipelineApiConfigClient>();
        mockClient.Setup(c => c.GetPipelineConfigAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PipelineConfiguration());

        var store = new ApiPipelineConfigStore(mockClient.Object) { CacheTtlSeconds = 60 };
        await store.LoadPipelineConfigAsync(CancellationToken.None);
        await store.LoadPipelineConfigAsync(CancellationToken.None);

        // Second call should use cache — client only invoked once
        mockClient.Verify(c => c.GetPipelineConfigAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task PipelineConfigStore_InvalidateCaches_ForcesRefetch()
    {
        var mockClient = new Mock<CodingAgentWebUI.Api.Client.IPipelineApiConfigClient>();
        mockClient.Setup(c => c.GetPipelineConfigAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PipelineConfiguration());

        var store = new ApiPipelineConfigStore(mockClient.Object) { CacheTtlSeconds = 60 };
        await store.LoadPipelineConfigAsync(CancellationToken.None);
        store.InvalidateCaches();
        await store.LoadPipelineConfigAsync(CancellationToken.None);

        mockClient.Verify(c => c.GetPipelineConfigAsync(It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    [Fact]
    public async Task PipelineConfigStore_SavePipelineConfigAsync_UpdatesCache()
    {
        var mockClient = new Mock<CodingAgentWebUI.Api.Client.IPipelineApiConfigClient>();
        mockClient.Setup(c => c.SavePipelineConfigAsync(It.IsAny<PipelineConfiguration>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var store = new ApiPipelineConfigStore(mockClient.Object) { CacheTtlSeconds = 60 };
        var config = new PipelineConfiguration();
        await store.SavePipelineConfigAsync(config, CancellationToken.None);

        // After save, cache should be warm — no client call on next load
        mockClient.Setup(c => c.GetPipelineConfigAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PipelineConfiguration());
        var result = await store.LoadPipelineConfigAsync(CancellationToken.None);
        result.Should().BeSameAs(config);
        mockClient.Verify(c => c.GetPipelineConfigAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task PipelineConfigStore_UpdatePipelineConfigAsync_InvalidatesCache()
    {
        var mockClient = new Mock<CodingAgentWebUI.Api.Client.IPipelineApiConfigClient>();
        mockClient.Setup(c => c.GetPipelineConfigAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PipelineConfiguration());
        mockClient.Setup(c => c.UpdatePipelineConfigAsync(It.IsAny<Func<PipelineConfiguration, PipelineConfiguration>>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var store = new ApiPipelineConfigStore(mockClient.Object) { CacheTtlSeconds = 60 };
        await store.LoadPipelineConfigAsync(CancellationToken.None); // populate cache
        await store.UpdatePipelineConfigAsync(c => c, CancellationToken.None); // should bust cache
        await store.LoadPipelineConfigAsync(CancellationToken.None); // should refetch

        mockClient.Verify(c => c.GetPipelineConfigAsync(It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    // ─────────────────────────────────────────────────────────────────────
    // ApiProviderConfigStore
    // ─────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ProviderConfigStore_LoadProviderConfigsAsync_FetchesWithSecrets()
    {
        var mockClient = new Mock<CodingAgentWebUI.Api.Client.IPipelineApiConfigClient>();
        var configs = new List<ProviderConfig>
        {
            new() { Id = "p1", Kind = ProviderKind.Issue, DisplayName = "GitHub", ProviderType = "GitHub" }
        } as IReadOnlyList<ProviderConfig>;
        mockClient.Setup(c => c.GetProviderConfigsWithSecretsAsync(ProviderKind.Issue, It.IsAny<CancellationToken>()))
            .ReturnsAsync(configs);

        var store = new ApiProviderConfigStore(mockClient.Object);
        var result = await store.LoadProviderConfigsAsync(ProviderKind.Issue, CancellationToken.None);

        result.Should().HaveCount(1);
        mockClient.Verify(c => c.GetProviderConfigsWithSecretsAsync(ProviderKind.Issue, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ProviderConfigStore_LoadProviderConfigsAsync_CachesByKind()
    {
        var mockClient = new Mock<CodingAgentWebUI.Api.Client.IPipelineApiConfigClient>();
        mockClient.Setup(c => c.GetProviderConfigsWithSecretsAsync(It.IsAny<ProviderKind>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ProviderConfig>() as IReadOnlyList<ProviderConfig>);

        var store = new ApiProviderConfigStore(mockClient.Object) { CacheTtlSeconds = 60 };
        await store.LoadProviderConfigsAsync(ProviderKind.Issue, CancellationToken.None);
        await store.LoadProviderConfigsAsync(ProviderKind.Issue, CancellationToken.None); // cached
        await store.LoadProviderConfigsAsync(ProviderKind.Agent, CancellationToken.None); // different kind — fetches

        mockClient.Verify(c => c.GetProviderConfigsWithSecretsAsync(ProviderKind.Issue, It.IsAny<CancellationToken>()), Times.Once);
        mockClient.Verify(c => c.GetProviderConfigsWithSecretsAsync(ProviderKind.Agent, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ProviderConfigStore_InvalidateCaches_ClearsAllKinds()
    {
        var mockClient = new Mock<CodingAgentWebUI.Api.Client.IPipelineApiConfigClient>();
        mockClient.Setup(c => c.GetProviderConfigsWithSecretsAsync(It.IsAny<ProviderKind>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ProviderConfig>() as IReadOnlyList<ProviderConfig>);

        var store = new ApiProviderConfigStore(mockClient.Object) { CacheTtlSeconds = 60 };
        await store.LoadProviderConfigsAsync(ProviderKind.Issue, CancellationToken.None);
        store.InvalidateCaches();
        await store.LoadProviderConfigsAsync(ProviderKind.Issue, CancellationToken.None);

        mockClient.Verify(c => c.GetProviderConfigsWithSecretsAsync(ProviderKind.Issue, It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    [Fact]
    public async Task ProviderConfigStore_GetProviderConfigByIdAsync_ReturnsMatchingConfig()
    {
        var mockClient = new Mock<CodingAgentWebUI.Api.Client.IPipelineApiConfigClient>();
        var configs = new List<ProviderConfig>
        {
            new() { Id = "p1", Kind = ProviderKind.Issue, DisplayName = "GitHub", ProviderType = "GitHub" },
            new() { Id = "p2", Kind = ProviderKind.Issue, DisplayName = "GitLab", ProviderType = "GitLab" }
        } as IReadOnlyList<ProviderConfig>;
        mockClient.Setup(c => c.GetProviderConfigsWithSecretsAsync(ProviderKind.Issue, It.IsAny<CancellationToken>()))
            .ReturnsAsync(configs);

        var store = new ApiProviderConfigStore(mockClient.Object);
        var result = await store.GetProviderConfigByIdAsync("p2", ProviderKind.Issue, CancellationToken.None);

        result!.Id.Should().Be("p2");
    }

    [Fact]
    public async Task ProviderConfigStore_GetProviderConfigByIdAsync_WhenNotFound_ReturnsNull()
    {
        var mockClient = new Mock<CodingAgentWebUI.Api.Client.IPipelineApiConfigClient>();
        mockClient.Setup(c => c.GetProviderConfigsWithSecretsAsync(It.IsAny<ProviderKind>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ProviderConfig>() as IReadOnlyList<ProviderConfig>);

        var store = new ApiProviderConfigStore(mockClient.Object);
        var result = await store.GetProviderConfigByIdAsync("missing", ProviderKind.Issue, CancellationToken.None);
        result.Should().BeNull();
    }

    [Fact]
    public async Task ProviderConfigStore_SaveProviderConfigAsync_InvalidatesCache()
    {
        var mockClient = new Mock<CodingAgentWebUI.Api.Client.IPipelineApiConfigClient>();
        mockClient.Setup(c => c.GetProviderConfigsWithSecretsAsync(It.IsAny<ProviderKind>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ProviderConfig>() as IReadOnlyList<ProviderConfig>);
        mockClient.Setup(c => c.SaveProviderConfigAsync(It.IsAny<ProviderConfig>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var store = new ApiProviderConfigStore(mockClient.Object) { CacheTtlSeconds = 60 };
        await store.LoadProviderConfigsAsync(ProviderKind.Issue, CancellationToken.None);
        await store.SaveProviderConfigAsync(new ProviderConfig { Id = "x", Kind = ProviderKind.Issue, DisplayName = "X", ProviderType = "X" }, CancellationToken.None);
        await store.LoadProviderConfigsAsync(ProviderKind.Issue, CancellationToken.None);

        mockClient.Verify(c => c.GetProviderConfigsWithSecretsAsync(ProviderKind.Issue, It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    // ─────────────────────────────────────────────────────────────────────
    // ProviderConfigCache
    // ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void ProviderConfigCache_TryGet_WhenEmpty_ReturnsFalse()
    {
        var cache = new ProviderConfigCache();
        var result = cache.TryGet(ProviderKind.Issue, out var configs);

        result.Should().BeFalse();
        configs.Should().BeEmpty();
    }

    [Fact]
    public void ProviderConfigCache_TryGet_AfterSet_ReturnsTrue()
    {
        var cache = new ProviderConfigCache();
        var list = new List<ProviderConfig>
        {
            new() { Id = "p1", Kind = ProviderKind.Issue, DisplayName = "X", ProviderType = "X" }
        } as IReadOnlyList<ProviderConfig>;
        cache.Set(ProviderKind.Issue, list, ttlSeconds: 60);

        var result = cache.TryGet(ProviderKind.Issue, out var configs);
        result.Should().BeTrue();
        configs.Should().HaveCount(1);
    }

    [Fact]
    public void ProviderConfigCache_TryGet_AfterExpiry_ReturnsFalse()
    {
        var cache = new ProviderConfigCache();
        var list = new List<ProviderConfig>() as IReadOnlyList<ProviderConfig>;
        cache.Set(ProviderKind.Issue, list, ttlSeconds: -1); // already expired

        var result = cache.TryGet(ProviderKind.Issue, out _);
        result.Should().BeFalse();
    }

    [Fact]
    public void ProviderConfigCache_Clear_RemovesAllKinds()
    {
        var cache = new ProviderConfigCache();
        var list = new List<ProviderConfig>() as IReadOnlyList<ProviderConfig>;
        cache.Set(ProviderKind.Issue, list, ttlSeconds: 60);
        cache.Set(ProviderKind.Agent, list, ttlSeconds: 60);

        cache.Clear();

        cache.TryGet(ProviderKind.Issue, out _).Should().BeFalse();
        cache.TryGet(ProviderKind.Agent, out _).Should().BeFalse();
    }

    [Fact]
    public void ProviderConfigCache_IsolatesKinds_DifferentKindsDoNotShareCache()
    {
        var cache = new ProviderConfigCache();
        var issueList = new List<ProviderConfig>
        {
            new() { Id = "issue-1", Kind = ProviderKind.Issue, DisplayName = "I", ProviderType = "I" }
        } as IReadOnlyList<ProviderConfig>;
        cache.Set(ProviderKind.Issue, issueList, ttlSeconds: 60);

        var hasAgent = cache.TryGet(ProviderKind.Agent, out var agentConfigs);

        hasAgent.Should().BeFalse();
        agentConfigs.Should().BeEmpty();
    }

    // ─────────────────────────────────────────────────────────────────────
    // ApiProjectStore
    // ─────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ProjectStore_LoadProjectsAsync_CallsClient()
    {
        var mockClient = new Mock<CodingAgentWebUI.Api.Client.IPipelineApiConfigClient>();
        mockClient.Setup(c => c.GetProjectsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<PipelineProject> { new() { Id = "p1", Name = "Proj" } } as IReadOnlyList<PipelineProject>);

        var store = new ApiProjectStore(mockClient.Object);
        var result = await store.LoadProjectsAsync(CancellationToken.None);

        result.Should().HaveCount(1);
    }

    [Fact]
    public async Task ProjectStore_LoadProjectsAsync_CachesResult()
    {
        var mockClient = new Mock<CodingAgentWebUI.Api.Client.IPipelineApiConfigClient>();
        mockClient.Setup(c => c.GetProjectsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<PipelineProject>() as IReadOnlyList<PipelineProject>);

        var store = new ApiProjectStore(mockClient.Object) { CacheTtlSeconds = 60 };
        await store.LoadProjectsAsync(CancellationToken.None);
        await store.LoadProjectsAsync(CancellationToken.None);

        mockClient.Verify(c => c.GetProjectsAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ProjectStore_SaveProjectAsync_InvalidatesCache()
    {
        var mockClient = new Mock<CodingAgentWebUI.Api.Client.IPipelineApiConfigClient>();
        mockClient.Setup(c => c.GetProjectsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<PipelineProject>() as IReadOnlyList<PipelineProject>);
        mockClient.Setup(c => c.SaveProjectAsync(It.IsAny<PipelineProject>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var store = new ApiProjectStore(mockClient.Object) { CacheTtlSeconds = 60 };
        await store.LoadProjectsAsync(CancellationToken.None);
        await store.SaveProjectAsync(new PipelineProject { Id = "x", Name = "X" }, CancellationToken.None);
        await store.LoadProjectsAsync(CancellationToken.None);

        mockClient.Verify(c => c.GetProjectsAsync(It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    [Fact]
    public async Task ProjectStore_DeleteProjectAsync_InvalidatesCache()
    {
        var mockClient = new Mock<CodingAgentWebUI.Api.Client.IPipelineApiConfigClient>();
        mockClient.Setup(c => c.GetProjectsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<PipelineProject>() as IReadOnlyList<PipelineProject>);
        mockClient.Setup(c => c.DeleteProjectAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var store = new ApiProjectStore(mockClient.Object) { CacheTtlSeconds = 60 };
        await store.LoadProjectsAsync(CancellationToken.None);
        await store.DeleteProjectAsync("p1", CancellationToken.None);
        await store.LoadProjectsAsync(CancellationToken.None);

        mockClient.Verify(c => c.GetProjectsAsync(It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    [Fact]
    public async Task ProjectStore_HasEnabledTemplatesAsync_DelegatesToClient()
    {
        var mockClient = new Mock<CodingAgentWebUI.Api.Client.IPipelineApiConfigClient>();
        mockClient.Setup(c => c.HasEnabledTemplatesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var store = new ApiProjectStore(mockClient.Object);
        var result = await store.HasEnabledTemplatesAsync(CancellationToken.None);

        result.Should().BeTrue();
    }

    [Fact]
    public async Task ProjectStore_LoadAllTemplatesAsync_CachesResult()
    {
        var mockClient = new Mock<CodingAgentWebUI.Api.Client.IPipelineApiConfigClient>();
        mockClient.Setup(c => c.GetAllTemplatesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<PipelineJobTemplate>() as IReadOnlyList<PipelineJobTemplate>);

        var store = new ApiProjectStore(mockClient.Object) { CacheTtlSeconds = 60 };
        await store.LoadAllTemplatesAsync(CancellationToken.None);
        await store.LoadAllTemplatesAsync(CancellationToken.None);

        mockClient.Verify(c => c.GetAllTemplatesAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ProjectStore_InvalidateCaches_ClearsBothProjectsAndTemplates()
    {
        var mockClient = new Mock<CodingAgentWebUI.Api.Client.IPipelineApiConfigClient>();
        mockClient.Setup(c => c.GetProjectsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<PipelineProject>() as IReadOnlyList<PipelineProject>);
        mockClient.Setup(c => c.GetAllTemplatesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<PipelineJobTemplate>() as IReadOnlyList<PipelineJobTemplate>);

        var store = new ApiProjectStore(mockClient.Object) { CacheTtlSeconds = 60 };
        await store.LoadProjectsAsync(CancellationToken.None);
        await store.LoadAllTemplatesAsync(CancellationToken.None);
        store.InvalidateCaches();
        await store.LoadProjectsAsync(CancellationToken.None);
        await store.LoadAllTemplatesAsync(CancellationToken.None);

        mockClient.Verify(c => c.GetProjectsAsync(It.IsAny<CancellationToken>()), Times.Exactly(2));
        mockClient.Verify(c => c.GetAllTemplatesAsync(It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    // ─────────────────────────────────────────────────────────────────────
    // ApiConfigurationStore — delegation and cross-invalidation
    // ─────────────────────────────────────────────────────────────────────

    private static (ApiConfigurationStore Store, Mock<CodingAgentWebUI.Api.Client.IPipelineApiConfigClient> Client) CreateConfigStore()
    {
        var mockClient = new Mock<CodingAgentWebUI.Api.Client.IPipelineApiConfigClient>();
        var pipeline = new ApiPipelineConfigStore(mockClient.Object) { CacheTtlSeconds = 60 };
        var providers = new ApiProviderConfigStore(mockClient.Object) { CacheTtlSeconds = 60 };
        var projects = new ApiProjectStore(mockClient.Object) { CacheTtlSeconds = 60 };
        var store = new ApiConfigurationStore(mockClient.Object, pipeline, providers, projects) { CacheTtlSeconds = 60 };
        return (store, mockClient);
    }

    [Fact]
    public async Task ConfigurationStore_LoadAgentProfilesAsync_CachesResult()
    {
        var (store, client) = CreateConfigStore();
        client.Setup(c => c.GetAgentProfilesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<AgentProfile>() as IReadOnlyList<AgentProfile>);

        await store.LoadAgentProfilesAsync(CancellationToken.None);
        await store.LoadAgentProfilesAsync(CancellationToken.None);

        client.Verify(c => c.GetAgentProfilesAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ConfigurationStore_SaveAgentProfileAsync_InvalidatesCache()
    {
        var (store, client) = CreateConfigStore();
        client.Setup(c => c.GetAgentProfilesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<AgentProfile>() as IReadOnlyList<AgentProfile>);
        client.Setup(c => c.SaveAgentProfileAsync(It.IsAny<AgentProfile>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        await store.LoadAgentProfilesAsync(CancellationToken.None);
        await store.SaveAgentProfileAsync(new AgentProfile { Id = "a", DisplayName = "A", AgentProviderConfigId = "k" }, CancellationToken.None);
        await store.LoadAgentProfilesAsync(CancellationToken.None);

        client.Verify(c => c.GetAgentProfilesAsync(It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    [Fact]
    public async Task ConfigurationStore_LoadQualityGateConfigsAsync_CachesResult()
    {
        var (store, client) = CreateConfigStore();
        client.Setup(c => c.GetQualityGateConfigsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<QualityGateConfiguration>() as IReadOnlyList<QualityGateConfiguration>);

        await store.LoadQualityGateConfigsAsync(CancellationToken.None);
        await store.LoadQualityGateConfigsAsync(CancellationToken.None);

        client.Verify(c => c.GetQualityGateConfigsAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ConfigurationStore_InvalidateCaches_ClearsAllComposedStores()
    {
        var (store, client) = CreateConfigStore();
        client.Setup(c => c.GetAgentProfilesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<AgentProfile>() as IReadOnlyList<AgentProfile>);
        client.Setup(c => c.GetPipelineConfigAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PipelineConfiguration());

        await store.LoadAgentProfilesAsync(CancellationToken.None);
        await store.LoadPipelineConfigAsync(CancellationToken.None);

        store.InvalidateCaches();

        await store.LoadAgentProfilesAsync(CancellationToken.None);
        await store.LoadPipelineConfigAsync(CancellationToken.None);

        client.Verify(c => c.GetAgentProfilesAsync(It.IsAny<CancellationToken>()), Times.Exactly(2));
        client.Verify(c => c.GetPipelineConfigAsync(It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    [Fact]
    public async Task ConfigurationStore_ResetReviewerConfigsToDefaultAsync_InvalidatesReviewerCache()
    {
        var (store, client) = CreateConfigStore();
        client.Setup(c => c.GetReviewerConfigsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ReviewerConfiguration>() as IReadOnlyList<ReviewerConfiguration>);
        client.Setup(c => c.ResetReviewerConfigsToDefaultAsync(It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        await store.LoadReviewerConfigsAsync(CancellationToken.None);
        await store.ResetReviewerConfigsToDefaultAsync(CancellationToken.None);
        await store.LoadReviewerConfigsAsync(CancellationToken.None);

        client.Verify(c => c.GetReviewerConfigsAsync(It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    // ─────────────────────────────────────────────────────────────────────
    // ApiBackedConsolidationRunStore
    // ─────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ConsolidationRunStore_SaveRunAsync_DelegatesToClient()
    {
        var mockClient = new Mock<CodingAgentWebUI.Api.Client.IPipelineApiConsolidationRunClient>();
        mockClient.Setup(c => c.SaveRunAsync(It.IsAny<ConsolidationRun>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var store = new ApiBackedConsolidationRunStore(mockClient.Object);
        var run = new ConsolidationRun { RunId = Guid.NewGuid().ToString(), Type = ConsolidationRunType.BrainConsolidation, StartedAtUtc = DateTimeOffset.UtcNow };
        await store.SaveRunAsync(run, CancellationToken.None);

        mockClient.Verify(c => c.SaveRunAsync(run, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ConsolidationRunStore_GetByIdAsync_DelegatesToClient()
    {
        var mockClient = new Mock<CodingAgentWebUI.Api.Client.IPipelineApiConsolidationRunClient>();
        mockClient.Setup(c => c.GetByIdAsync("run-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync((ConsolidationRun?)null);

        var store = new ApiBackedConsolidationRunStore(mockClient.Object);
        var result = await store.GetByIdAsync(new RunId("run-1"), CancellationToken.None);

        result.Should().BeNull();
    }

    [Fact]
    public async Task ConsolidationRunStore_DeleteRunAsync_DelegatesToClient()
    {
        var mockClient = new Mock<CodingAgentWebUI.Api.Client.IPipelineApiConsolidationRunClient>();
        mockClient.Setup(c => c.DeleteRunAsync("run-1", It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var store = new ApiBackedConsolidationRunStore(mockClient.Object);
        await store.DeleteRunAsync(new RunId("run-1"), CancellationToken.None);

        mockClient.Verify(c => c.DeleteRunAsync("run-1", It.IsAny<CancellationToken>()), Times.Once);
    }

    // ─────────────────────────────────────────────────────────────────────
    // ApiBackedHarnessSuggestionStore
    // ─────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task HarnessSuggestionStore_GetAsync_DelegatesToClient()
    {
        var mockClient = new Mock<CodingAgentWebUI.Api.Client.IPipelineApiHarnessSuggestionClient>();
        mockClient.Setup(c => c.GetAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync((HarnessSuggestions?)null);

        var store = new ApiBackedHarnessSuggestionStore(mockClient.Object);
        var result = await store.GetAsync(CancellationToken.None);

        result.Should().BeNull();
    }

    [Fact]
    public async Task HarnessSuggestionStore_SaveAsync_DelegatesToClient()
    {
        var mockClient = new Mock<CodingAgentWebUI.Api.Client.IPipelineApiHarnessSuggestionClient>();
        mockClient.Setup(c => c.SaveAsync(It.IsAny<HarnessSuggestions>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var store = new ApiBackedHarnessSuggestionStore(mockClient.Object);
        var suggestions = new HarnessSuggestions
        {
            BasedOnRunCount = 1,
            GeneratedAtUtc = DateTime.UtcNow,
            SuccessRate = 0.5m,
            Suggestions = []
        };
        await store.SaveAsync(suggestions, CancellationToken.None);

        mockClient.Verify(c => c.SaveAsync(It.IsAny<HarnessSuggestions>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    // ─────────────────────────────────────────────────────────────────────
    // ApiBackedPendingWorkQuery
    // ─────────────────────────────────────────────────────────────────────

    private static PendingWorkItemDto MakePendingDto(string id = "GH-1") => new()
    {
        Id = Guid.NewGuid(),
        IssueIdentifier = id,
        IssueProviderConfigId = "github",
        TaskType = WorkItemTaskType.Implementation,
        CreatedAt = DateTimeOffset.UtcNow,
        AgentSelector = "kiro",
        RetryCount = 0,
        TimeoutSeconds = 0
    };

    [Fact]
    public async Task PendingWorkQuery_GetPendingJobsAsync_MapsDtosToPendingJobs()
    {
        var mockClient = new Mock<CodingAgentWebUI.Api.Client.IPipelineApiWorkItemClient>();
        mockClient.Setup(c => c.GetPendingAsync(200, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<PendingWorkItemDto> { MakePendingDto("GH-42") } as IReadOnlyList<PendingWorkItemDto>);

        var query = new ApiBackedPendingWorkQuery(mockClient.Object);
        var result = await query.GetPendingJobsAsync();

        result.Should().HaveCount(1);
        result[0].IssueIdentifier.Value.Should().Be("GH-42");
        query.PendingCount.Should().Be(1);
    }

    [Fact]
    public async Task PendingWorkQuery_GetPendingJobsAsync_UpdatesPendingCount()
    {
        var mockClient = new Mock<CodingAgentWebUI.Api.Client.IPipelineApiWorkItemClient>();
        mockClient.Setup(c => c.GetPendingAsync(200, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<PendingWorkItemDto> { MakePendingDto(), MakePendingDto() } as IReadOnlyList<PendingWorkItemDto>);

        var query = new ApiBackedPendingWorkQuery(mockClient.Object);
        await query.GetPendingJobsAsync();

        query.PendingCount.Should().Be(2);
    }

    [Fact]
    public async Task PendingWorkQuery_GetPendingJobsAsync_SplitsAgentSelectorOnComma()
    {
        var mockClient = new Mock<CodingAgentWebUI.Api.Client.IPipelineApiWorkItemClient>();
        var dto = new PendingWorkItemDto
        {
            Id = Guid.NewGuid(),
            IssueIdentifier = "GH-1",
            IssueProviderConfigId = "github",
            TaskType = WorkItemTaskType.Implementation,
            CreatedAt = DateTimeOffset.UtcNow,
            AgentSelector = "kiro, dotnet",
            RetryCount = 0,
            TimeoutSeconds = 0
        };
        mockClient.Setup(c => c.GetPendingAsync(200, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<PendingWorkItemDto> { dto } as IReadOnlyList<PendingWorkItemDto>);

        var query = new ApiBackedPendingWorkQuery(mockClient.Object);
        var result = await query.GetPendingJobsAsync();

        result[0].RequiredLabels.Should().BeEquivalentTo(["kiro", "dotnet"]);
    }

    [Fact]
    public async Task PendingWorkQuery_ConsolidationTask_MapsToConsolidationRunType()
    {
        var mockClient = new Mock<CodingAgentWebUI.Api.Client.IPipelineApiWorkItemClient>();
        var dto = new PendingWorkItemDto
        {
            Id = Guid.NewGuid(),
            IssueIdentifier = "C-1",
            IssueProviderConfigId = ConsolidationConstants.ProviderConfigId,
            TaskType = WorkItemTaskType.Consolidation,
            CreatedAt = DateTimeOffset.UtcNow,
            AgentSelector = "kiro",
            RetryCount = 0,
            TimeoutSeconds = 0
        };
        mockClient.Setup(c => c.GetPendingAsync(200, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<PendingWorkItemDto> { dto } as IReadOnlyList<PendingWorkItemDto>);

        var query = new ApiBackedPendingWorkQuery(mockClient.Object);
        var result = await query.GetPendingJobsAsync();

        result[0].RunType.Should().Be(PipelineRunType.Consolidation);
    }

    [Fact]
    public async Task PendingWorkQuery_ReviewTask_MapsToReviewRunType()
    {
        var mockClient = new Mock<CodingAgentWebUI.Api.Client.IPipelineApiWorkItemClient>();
        var dto = new PendingWorkItemDto
        {
            Id = Guid.NewGuid(),
            IssueIdentifier = "PR-42",
            IssueProviderConfigId = "github",
            TaskType = WorkItemTaskType.Review,
            CreatedAt = DateTimeOffset.UtcNow,
            AgentSelector = "kiro",
            RetryCount = 0,
            TimeoutSeconds = 0
        };
        mockClient.Setup(c => c.GetPendingAsync(200, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<PendingWorkItemDto> { dto } as IReadOnlyList<PendingWorkItemDto>);

        var query = new ApiBackedPendingWorkQuery(mockClient.Object);
        var result = await query.GetPendingJobsAsync();

        result.Should().HaveCount(1, because: "exactly one pending item was returned by the mock");
        result[0].RunType.Should().Be(PipelineRunType.Review,
            because: "a queued Review job must display the Review badge, not the Impl badge (regression: #2159)");
    }

    [Fact]
    public async Task PendingWorkQuery_DecompositionTask_MapsToDecompositionAnalysisRunType()
    {
        var mockClient = new Mock<CodingAgentWebUI.Api.Client.IPipelineApiWorkItemClient>();
        var dto = new PendingWorkItemDto
        {
            Id = Guid.NewGuid(),
            IssueIdentifier = "GH-100",
            IssueProviderConfigId = "github",
            TaskType = WorkItemTaskType.Decomposition,
            CreatedAt = DateTimeOffset.UtcNow,
            AgentSelector = "kiro",
            RetryCount = 0,
            TimeoutSeconds = 0
        };
        mockClient.Setup(c => c.GetPendingAsync(200, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<PendingWorkItemDto> { dto } as IReadOnlyList<PendingWorkItemDto>);

        var query = new ApiBackedPendingWorkQuery(mockClient.Object);
        var result = await query.GetPendingJobsAsync();

        result.Should().HaveCount(1, because: "exactly one pending item was returned by the mock");
        result[0].RunType.Should().Be(PipelineRunType.DecompositionAnalysis,
            because: "a pending Decomposition job is always Phase 1 (analysis) — must display 'Decomp (A)', not 'Impl' (regression: #2159)");
    }

    [Fact]
    public void PendingWorkQuery_PendingCountInitiallyZero()
    {
        var mockClient = new Mock<CodingAgentWebUI.Api.Client.IPipelineApiWorkItemClient>();
        var query = new ApiBackedPendingWorkQuery(mockClient.Object);

        query.PendingCount.Should().Be(0);
    }

    // ─────────────────────────────────────────────────────────────────────
    // ApiBackedPipelineRunHistoryService
    // ─────────────────────────────────────────────────────────────────────

    private static ApiBackedPipelineRunHistoryService CreateHistoryService(
        CodingAgentWebUI.Api.Client.IPipelineApiRunHistoryClient client)
        => new(client, Serilog.Log.Logger);

    private static PipelineRun MakeRun(string runId = "run-1") =>
        PipelineRun.CreateImplementation(new PipelineRunCreationParams
        {
            RunId = runId,
            IssueIdentifier = "GH-1",
            IssueTitle = "Test",
            IssueProviderConfigId = "github",
            RepoProviderConfigId = "github-repo",
            AgentId = "agent-1",
            AgentProviderConfigId = "kiro",
            InitiatedBy = "test",
            StartedAt = DateTimeOffset.UtcNow
        });

    [Fact]
    public async Task HistoryService_AddRunToHistoryAsync_NullThrows()
    {
        var client = new Mock<CodingAgentWebUI.Api.Client.IPipelineApiRunHistoryClient>();
        var svc = CreateHistoryService(client.Object);

        var act = () => svc.AddRunToHistoryAsync(null!);
        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    [Fact]
    public async Task HistoryService_AddRunToHistoryAsync_SkipsConsolidationRun()
    {
        var client = new Mock<CodingAgentWebUI.Api.Client.IPipelineApiRunHistoryClient>();
        var svc = CreateHistoryService(client.Object);

        var consolidationRun = PipelineRun.CreateImplementation(new PipelineRunCreationParams
        {
            RunId = "c-1",
            IssueIdentifier = "CONSOL",
            IssueTitle = "Consolidation",
            IssueProviderConfigId = ConsolidationConstants.ProviderConfigId,
            RepoProviderConfigId = "repo",
            AgentId = "agent-1",
            AgentProviderConfigId = "kiro",
            InitiatedBy = "test",
            StartedAt = DateTimeOffset.UtcNow
        });
        consolidationRun.CurrentStep = PipelineStep.Completed;

        await svc.AddRunToHistoryAsync(consolidationRun);

        client.Verify(c => c.AddRunToHistoryAsync(It.IsAny<PipelineRunSummary>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task HistoryService_AddRunToHistoryAsync_WhenNonTerminal_ForcesFailedStep()
    {
        var client = new Mock<CodingAgentWebUI.Api.Client.IPipelineApiRunHistoryClient>();
        client.Setup(c => c.AddRunToHistoryAsync(It.IsAny<PipelineRunSummary>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var svc = CreateHistoryService(client.Object);
        var run = MakeRun(); // CurrentStep = Created (non-terminal)

        await svc.AddRunToHistoryAsync(run);

        client.Verify(c => c.AddRunToHistoryAsync(
            It.Is<PipelineRunSummary>(s => s.FinalStep == PipelineStep.Failed),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task HistoryService_AddRunToHistoryAsync_WhenTerminal_UsesActualStep()
    {
        var client = new Mock<CodingAgentWebUI.Api.Client.IPipelineApiRunHistoryClient>();
        client.Setup(c => c.AddRunToHistoryAsync(It.IsAny<PipelineRunSummary>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var svc = CreateHistoryService(client.Object);
        var run = MakeRun();
        run.CurrentStep = PipelineStep.Completed; // set terminal step directly
        run.MarkCompleted();

        await svc.AddRunToHistoryAsync(run);

        client.Verify(c => c.AddRunToHistoryAsync(
            It.Is<PipelineRunSummary>(s => s.FinalStep == PipelineStep.Completed),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task HistoryService_AddRunToHistoryAsync_WhenClientThrows_DoesNotPropagate()
    {
        var client = new Mock<CodingAgentWebUI.Api.Client.IPipelineApiRunHistoryClient>();
        client.Setup(c => c.AddRunToHistoryAsync(It.IsAny<PipelineRunSummary>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("API down"));

        var svc = CreateHistoryService(client.Object);
        var run = MakeRun();
        run.MarkCompleted();

        var act = () => svc.AddRunToHistoryAsync(run);
        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task HistoryService_GetRunHistoryAsync_DelegatesToClient()
    {
        var client = new Mock<CodingAgentWebUI.Api.Client.IPipelineApiRunHistoryClient>();
        var page = new PagedResult<PipelineRunSummary> { Items = [], Page = 1, PageSize = 1000, HasMore = false };
        client.Setup(c => c.GetRunHistoryAsync(1, 1000, false, false, It.IsAny<PipelineStep?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(page);

        var svc = CreateHistoryService(client.Object);
        var result = await svc.GetRunHistoryAsync();

        result.Should().BeEmpty();
    }

    [Fact]
    public void HistoryService_TryDeleteWorkspace_IsNoOp()
    {
        var client = new Mock<CodingAgentWebUI.Api.Client.IPipelineApiRunHistoryClient>();
        var svc = CreateHistoryService(client.Object);

        svc.TryDeleteWorkspace("/some/path", "run-1", "/base");

        // No-op: API-backed history service never touches the local filesystem
        client.VerifyNoOtherCalls();
    }

    [Fact]
    public void HistoryService_CleanupExpiredWorkspaces_IsNoOp()
    {
        var client = new Mock<CodingAgentWebUI.Api.Client.IPipelineApiRunHistoryClient>();
        var svc = CreateHistoryService(client.Object);

        svc.CleanupExpiredWorkspaces(new PipelineConfiguration(), "run-1");

        // No-op: API-backed history service never touches the local filesystem
        client.VerifyNoOtherCalls();
    }

    // ─────────────────────────────────────────────────────────────────────
    // ApiBackedWorkItemFallbackTransitionService
    // ─────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task WorkItemFallbackService_TryFallbackChainAsync_OnSuccess_ReturnsTrue()
    {
        var client = new Mock<CodingAgentWebUI.Api.Client.IPipelineApiWorkItemClient>();
        client.Setup(c => c.PostStatusAsync(It.IsAny<Guid>(), It.IsAny<WorkItemStatusUpdate>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var svc = new ApiBackedWorkItemFallbackTransitionService(client.Object, Serilog.Log.Logger);
        var result = await svc.TryFallbackChainAsync(Guid.NewGuid(), WorkItemStatus.Failed, "error", null, CancellationToken.None);

        result.Should().BeTrue();
    }

    [Fact]
    public async Task WorkItemFallbackService_TryFallbackChainAsync_On400_ReturnsFalse()
    {
        var client = new Mock<CodingAgentWebUI.Api.Client.IPipelineApiWorkItemClient>();
        client.Setup(c => c.PostStatusAsync(It.IsAny<Guid>(), It.IsAny<WorkItemStatusUpdate>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("Bad Request", null, HttpStatusCode.BadRequest));

        var svc = new ApiBackedWorkItemFallbackTransitionService(client.Object, Serilog.Log.Logger);
        var result = await svc.TryFallbackChainAsync(Guid.NewGuid(), WorkItemStatus.Failed, null, null, CancellationToken.None);

        result.Should().BeFalse();
    }

    [Fact]
    public async Task WorkItemFallbackService_TryFallbackChainAsync_On404_ReturnsFalse()
    {
        var client = new Mock<CodingAgentWebUI.Api.Client.IPipelineApiWorkItemClient>();
        client.Setup(c => c.PostStatusAsync(It.IsAny<Guid>(), It.IsAny<WorkItemStatusUpdate>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("Not Found", null, HttpStatusCode.NotFound));

        var svc = new ApiBackedWorkItemFallbackTransitionService(client.Object, Serilog.Log.Logger);
        var result = await svc.TryFallbackChainAsync(Guid.NewGuid(), WorkItemStatus.Failed, null, null, CancellationToken.None);

        result.Should().BeFalse();
    }

    [Fact]
    public async Task WorkItemFallbackService_TryFallbackChainAsync_OnOtherException_Rethrows()
    {
        var client = new Mock<CodingAgentWebUI.Api.Client.IPipelineApiWorkItemClient>();
        client.Setup(c => c.PostStatusAsync(It.IsAny<Guid>(), It.IsAny<WorkItemStatusUpdate>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("Server Error", null, HttpStatusCode.InternalServerError));

        var svc = new ApiBackedWorkItemFallbackTransitionService(client.Object, Serilog.Log.Logger);
        var act = () => svc.TryFallbackChainAsync(Guid.NewGuid(), WorkItemStatus.Failed, null, null, CancellationToken.None);

        await act.Should().ThrowAsync<HttpRequestException>();
    }

    [Fact]
    public void WorkItemFallbackService_NullClient_Throws()
    {
        var act = () => new ApiBackedWorkItemFallbackTransitionService(null!, Serilog.Log.Logger);
        act.Should().Throw<ArgumentNullException>();
    }

    // ─────────────────────────────────────────────────────────────────────
    // ApiChatJobDispatcher
    // ─────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ChatJobDispatcher_DispatchChatPodAsync_ReturnsAgentId()
    {
        var client = new Mock<CodingAgentWebUI.Api.Client.IPipelineApiChatClient>();
        client.Setup(c => c.DispatchChatPodAsync("kiro", null, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync("agent-abc");

        var dispatcher = new ApiChatJobDispatcher(client.Object);
        var result = await dispatcher.DispatchChatPodAsync("kiro", null, null, CancellationToken.None);

        result.Should().Be("agent-abc");
    }

    [Fact]
    public async Task ChatJobDispatcher_DispatchChatPodAsync_On503_ThrowsNoPvcAvailableException()
    {
        var client = new Mock<CodingAgentWebUI.Api.Client.IPipelineApiChatClient>();
        client.Setup(c => c.DispatchChatPodAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("Unavailable", null, HttpStatusCode.ServiceUnavailable));

        var dispatcher = new ApiChatJobDispatcher(client.Object);
        var act = () => dispatcher.DispatchChatPodAsync("kiro", null, null, CancellationToken.None);

        await act.Should().ThrowAsync<NoPvcAvailableException>();
    }

    [Fact]
    public async Task ChatJobDispatcher_DispatchChatPodAsync_On504_ThrowsChatPodTimeoutException()
    {
        var client = new Mock<CodingAgentWebUI.Api.Client.IPipelineApiChatClient>();
        client.Setup(c => c.DispatchChatPodAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("Gateway Timeout", null, HttpStatusCode.GatewayTimeout));

        var dispatcher = new ApiChatJobDispatcher(client.Object);
        var act = () => dispatcher.DispatchChatPodAsync("kiro", null, null, CancellationToken.None);

        await act.Should().ThrowAsync<ChatPodTimeoutException>();
    }

    [Fact]
    public async Task ChatJobDispatcher_TerminateChatSessionAsync_DelegatesToClient()
    {
        var client = new Mock<CodingAgentWebUI.Api.Client.IPipelineApiChatClient>();
        client.Setup(c => c.TerminateChatSessionAsync("agent-1", It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var dispatcher = new ApiChatJobDispatcher(client.Object);
        await dispatcher.TerminateChatSessionAsync(new AgentId("agent-1"), CancellationToken.None);

        client.Verify(c => c.TerminateChatSessionAsync("agent-1", It.IsAny<CancellationToken>()), Times.Once);
    }
}
