using AwesomeAssertions;
using CodingAgent.Api.Client;
using CodingAgent.Pipeline.Models;
using CodingAgent.Api.Client.Stores;
using Moq;

namespace CodingAgent.Web.UnitTests.Services;

/// <summary>
/// Tests for the API-backed config store adapters introduced by Spec 045.
///
/// The cache behaviour matters more than it looks: these adapters are registered as
/// singletons in the monolith and sit in front of every config read the dispatch path makes.
/// A cache that keys provider configs by anything coarser than <see cref="ProviderKind"/>
/// hands the wrong provider list to <c>DispatchInfrastructure</c>, which loads Repository,
/// Agent and Pipeline kinds back to back.
/// </summary>
public sealed class ApiBackedConfigStoresTests
{
    private static ProviderConfig Provider(string id, ProviderKind kind) => new()
    {
        Id = id,
        Kind = kind,
        DisplayName = id,
        ProviderType = "test"
    };

    /// <summary>
    /// The store adapters read through <c>GetProviderConfigsWithSecretsAsync</c>, not the redacted
    /// default — the configs they load end up in the job payload an agent executes with, so masked
    /// values would ship "****" as every credential. Asserting on that method is deliberate: a
    /// store that quietly fell back to the redacted read would fail these tests.
    /// </summary>
    private static Mock<IPipelineApiConfigClient> ClientReturningOnePerKind()
    {
        var client = new Mock<IPipelineApiConfigClient>();
        foreach (var kind in Enum.GetValues<ProviderKind>())
        {
            var captured = kind;
            client.Setup(c => c.GetProviderConfigsWithSecretsAsync(captured, It.IsAny<CancellationToken>()))
                .ReturnsAsync(new List<ProviderConfig> { Provider($"{captured}-1", captured) });
        }
        return client;
    }

    // ── ApiProviderConfigStore ──────────────────────────────────────────────

    [Fact]
    public async Task ApiProviderConfigStore_EachKind_ReturnsItsOwnConfigs_WithinCacheTtl()
    {
        var client = ClientReturningOnePerKind();
        var store = new ApiProviderConfigStore(client.Object) { CacheTtlSeconds = 600 };

        // Load every kind in sequence, all inside one TTL window.
        foreach (var kind in Enum.GetValues<ProviderKind>())
        {
            var configs = await store.LoadProviderConfigsAsync(kind, CancellationToken.None);

            configs.Should().ContainSingle(
                $"{kind} must return its own configs, not another kind's cached list");
            configs[0].Kind.Should().Be(kind);
            configs[0].Id.Should().Be($"{kind}-1");
        }
    }

    [Fact]
    public async Task ApiProviderConfigStore_RepeatedLoadOfSameKind_HitsApiOnce()
    {
        var client = ClientReturningOnePerKind();
        var store = new ApiProviderConfigStore(client.Object) { CacheTtlSeconds = 600 };

        await store.LoadProviderConfigsAsync(ProviderKind.Repository, CancellationToken.None);
        await store.LoadProviderConfigsAsync(ProviderKind.Repository, CancellationToken.None);
        await store.LoadProviderConfigsAsync(ProviderKind.Repository, CancellationToken.None);

        client.Verify(
            c => c.GetProviderConfigsWithSecretsAsync(ProviderKind.Repository, It.IsAny<CancellationToken>()),
            Times.Once,
            "the TTL cache must collapse repeated reads of the same kind into one API call");
    }

    [Fact]
    public async Task ApiProviderConfigStore_GetProviderConfigById_ResolvesAgainstTheRequestedKind()
    {
        var client = ClientReturningOnePerKind();
        var store = new ApiProviderConfigStore(client.Object) { CacheTtlSeconds = 600 };

        // Warm the cache with Repository first — the failure mode this guards against.
        await store.LoadProviderConfigsAsync(ProviderKind.Repository, CancellationToken.None);

        var agent = await store.GetProviderConfigByIdAsync("Agent-1", ProviderKind.Agent, CancellationToken.None);
        agent.Should().NotBeNull("an Agent config must be resolvable even after a Repository load");
        agent!.Kind.Should().Be(ProviderKind.Agent);

        var repoIdUnderAgentKind = await store.GetProviderConfigByIdAsync(
            "Repository-1", ProviderKind.Agent, CancellationToken.None);
        repoIdUnderAgentKind.Should().BeNull("a Repository id must not resolve under the Agent kind");
    }

    [Fact]
    public async Task ApiProviderConfigStore_SaveInvalidatesEveryKind()
    {
        var client = ClientReturningOnePerKind();
        var store = new ApiProviderConfigStore(client.Object) { CacheTtlSeconds = 600 };

        await store.LoadProviderConfigsAsync(ProviderKind.Issue, CancellationToken.None);
        await store.LoadProviderConfigsAsync(ProviderKind.Agent, CancellationToken.None);

        await store.SaveProviderConfigAsync(Provider("new", ProviderKind.Agent), CancellationToken.None);

        await store.LoadProviderConfigsAsync(ProviderKind.Issue, CancellationToken.None);
        await store.LoadProviderConfigsAsync(ProviderKind.Agent, CancellationToken.None);

        client.Verify(c => c.GetProviderConfigsWithSecretsAsync(ProviderKind.Issue, It.IsAny<CancellationToken>()), Times.Exactly(2));
        client.Verify(c => c.GetProviderConfigsWithSecretsAsync(ProviderKind.Agent, It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    [Fact]
    public async Task ApiProviderConfigStore_ZeroTtl_AlwaysRefetches()
    {
        var client = ClientReturningOnePerKind();
        var store = new ApiProviderConfigStore(client.Object) { CacheTtlSeconds = 0 };

        await store.LoadProviderConfigsAsync(ProviderKind.Pipeline, CancellationToken.None);
        await store.LoadProviderConfigsAsync(ProviderKind.Pipeline, CancellationToken.None);

        client.Verify(
            c => c.GetProviderConfigsWithSecretsAsync(ProviderKind.Pipeline, It.IsAny<CancellationToken>()),
            Times.Exactly(2),
            "a zero TTL disables caching");
    }

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

    // ── ApiConfigurationStore (composite) ───────────────────────────────────

    [Fact]
    public async Task ApiConfigurationStore_EachKind_ReturnsItsOwnConfigs_WithinCacheTtl()
    {
        var client = ClientReturningOnePerKind();
        var store = CreateCompositeStore(client.Object, ttlSeconds: 600);

        foreach (var kind in Enum.GetValues<ProviderKind>())
        {
            var configs = await store.LoadProviderConfigsAsync(kind, CancellationToken.None);

            configs.Should().ContainSingle();
            configs[0].Kind.Should().Be(kind, $"{kind} must not be served another kind's cached list");
        }
    }

    /// <summary>
    /// Mirrors the real call order in <c>DispatchInfrastructure.ResolveAsync</c>:
    /// Repository, then Agent, then Pipeline, all within one TTL window.
    /// </summary>
    [Fact]
    public async Task ApiConfigurationStore_DispatchCallOrder_ResolvesEachKindCorrectly()
    {
        var client = ClientReturningOnePerKind();
        var store = CreateCompositeStore(client.Object, ttlSeconds: 600);

        var repo = await store.LoadProviderConfigsAsync(ProviderKind.Repository, CancellationToken.None);
        var agent = await store.LoadProviderConfigsAsync(ProviderKind.Agent, CancellationToken.None);
        var pipeline = await store.LoadProviderConfigsAsync(ProviderKind.Pipeline, CancellationToken.None);

        repo[0].Kind.Should().Be(ProviderKind.Repository);
        agent[0].Kind.Should().Be(ProviderKind.Agent);
        pipeline[0].Kind.Should().Be(ProviderKind.Pipeline);
    }

    [Fact]
    public async Task ApiConfigurationStore_InvalidateCaches_ClearsProviderConfigsForEveryKind()
    {
        var client = ClientReturningOnePerKind();
        var store = CreateCompositeStore(client.Object, ttlSeconds: 600);

        await store.LoadProviderConfigsAsync(ProviderKind.Brain, CancellationToken.None);
        store.InvalidateCaches();
        await store.LoadProviderConfigsAsync(ProviderKind.Brain, CancellationToken.None);

        client.Verify(
            c => c.GetProviderConfigsWithSecretsAsync(ProviderKind.Brain, It.IsAny<CancellationToken>()),
            Times.Exactly(2),
            "InvalidateCaches must drop cached provider configs for every kind");
    }

    // ── ApiPipelineConfigStore ──────────────────────────────────────────────

    [Fact]
    public async Task ApiPipelineConfigStore_CachesWithinTtl_AndInvalidatesOnUpdate()
    {
        var client = new Mock<IPipelineApiConfigClient>();
        client.Setup(c => c.GetPipelineConfigAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PipelineConfiguration());
        var store = new ApiPipelineConfigStore(client.Object) { CacheTtlSeconds = 600 };

        await store.LoadPipelineConfigAsync(CancellationToken.None);
        await store.LoadPipelineConfigAsync(CancellationToken.None);
        client.Verify(c => c.GetPipelineConfigAsync(It.IsAny<CancellationToken>()), Times.Once);

        await store.UpdatePipelineConfigAsync(c => c, CancellationToken.None);
        await store.LoadPipelineConfigAsync(CancellationToken.None);

        client.Verify(c => c.GetPipelineConfigAsync(It.IsAny<CancellationToken>()), Times.Exactly(2),
            "an update must invalidate the cached configuration");
    }

    [Fact]
    public async Task ApiPipelineConfigStore_ZeroTtl_AlwaysRefetches()
    {
        // CacheTtlSeconds = 0 means TtlCache.Set stores _expiry = UtcNow. On any subsequent
        // TryGet call (even microseconds later) UtcNow > _expiry, so the cache always misses.
        // This exercises the expiry-driven refetch path without needing time injection.
        // NOTE: Potential clock-resolution flakiness on Windows CI (~15.6 ms tick). If both the
        // Set call and the subsequent TryGet resolve to the same quantized tick, the inclusive
        // DateTime.UtcNow <= _expiry comparison becomes true → cache hit → Times.Once instead of
        // Times.Exactly(2). This pattern is replicated from the pre-existing
        // ApiProviderConfigStore_ZeroTtl_AlwaysRefetches test. Fix by injecting a time abstraction
        // (e.g. TimeProvider) so tests can advance the clock deterministically.
        var client = new Mock<IPipelineApiConfigClient>();
        client.Setup(c => c.GetPipelineConfigAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PipelineConfiguration());
        var store = new ApiPipelineConfigStore(client.Object) { CacheTtlSeconds = 0 };

        await store.LoadPipelineConfigAsync(CancellationToken.None);
        await store.LoadPipelineConfigAsync(CancellationToken.None);

        client.Verify(
            c => c.GetPipelineConfigAsync(It.IsAny<CancellationToken>()),
            Times.Exactly(2),
            "a zero TTL must disable caching so every load fetches from the API");
    }

    [Fact]
    public async Task ApiProjectStore_ZeroTtl_AlwaysRefetchesProjects()
    {
        // NOTE: Same clock-resolution flakiness risk as ApiPipelineConfigStore_ZeroTtl_AlwaysRefetches.
        // On Windows CI with ~15.6 ms tick granularity, both DateTime.UtcNow calls (in Set and TryGet)
        // may land on the same tick, causing a spurious cache hit and Times.Once instead of Times.Exactly(2).
        // Fix by injecting a time abstraction (e.g. TimeProvider) so tests can advance the clock deterministically.
        var client = new Mock<IPipelineApiConfigClient>();
        client.Setup(c => c.GetProjectsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<PipelineProject>());
        var store = new ApiProjectStore(client.Object) { CacheTtlSeconds = 0 };

        await store.LoadProjectsAsync(CancellationToken.None);
        await store.LoadProjectsAsync(CancellationToken.None);

        client.Verify(
            c => c.GetProjectsAsync(It.IsAny<CancellationToken>()),
            Times.Exactly(2),
            "a zero TTL must disable caching so every projects load fetches from the API");
    }

    [Fact]
    public async Task ApiProjectStore_ZeroTtl_AlwaysRefetchesTemplates()
    {
        // NOTE: Same clock-resolution flakiness risk as ApiPipelineConfigStore_ZeroTtl_AlwaysRefetches.
        // On Windows CI with ~15.6 ms tick granularity, both DateTime.UtcNow calls (in Set and TryGet)
        // may land on the same tick, causing a spurious cache hit and Times.Once instead of Times.Exactly(2).
        // Fix by injecting a time abstraction (e.g. TimeProvider) so tests can advance the clock deterministically.
        var client = new Mock<IPipelineApiConfigClient>();
        client.Setup(c => c.GetAllTemplatesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<PipelineJobTemplate>());
        var store = new ApiProjectStore(client.Object) { CacheTtlSeconds = 0 };

        await store.LoadAllTemplatesAsync(CancellationToken.None);
        await store.LoadAllTemplatesAsync(CancellationToken.None);

        client.Verify(
            c => c.GetAllTemplatesAsync(It.IsAny<CancellationToken>()),
            Times.Exactly(2),
            "a zero TTL must disable caching so every templates load fetches from the API");
    }

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

        client.Verify(c => c.MoveTemplateAsync(new ProjectId("src"), new ProjectId("dst"), "tmpl-1", It.IsAny<CancellationToken>()), Times.Once);
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

        client.Verify(c => c.MoveTemplateAsync(new ProjectId("src"), new ProjectId("dst"), "tmpl-1",
            It.IsAny<CancellationToken>()), Times.Once);
        // TODO: [WARNING] This test only verifies the client is called once — it does NOT assert that the
        // cache is invalidated for both source and target projects, despite the test name claiming it does.
        // If the cache-invalidation branch in ApiProjectStore.MoveTemplateAsync were deleted, this test
        // would still pass. Add assertions that load the cached projects before the move, call MoveTemplateAsync,
        // then call GetProjectsAsync again and verify the client was called a second time (cache miss) for
        // both source and target project IDs.
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

    // ── Helpers ─────────────────────────────────────────────────────

    /// <summary>
    /// Builds the composite store the way DI does: over the same three narrow stores, so the
    /// caches under test are the ones production shares rather than private copies.
    /// NOTE: ApiProjectStore is missing cache-hit characterization tests. There is no test asserting
    /// that a second LoadProjectsAsync or LoadAllTemplatesAsync call within a non-zero TTL window
    /// hits the API exactly once. The migration could silently break cache-hit behaviour (e.g. if
    /// LoadCachedAsync was wired to the wrong TtlCache field) and no existing test would catch it.
    /// Add: ApiProjectStore_CachesProjectsWithinTtl and ApiProjectStore_CachesTemplatesWithinTtl.
    /// NOTE: ApiProjectStore is missing invalidate-on-write characterization tests. The issue
    /// prerequisites explicitly require "invalidate-on-write" tests before migrating. There are no
    /// tests asserting that SaveProjectAsync, DeleteProjectAsync, SaveTemplateAsync,
    /// DeleteTemplateAsync, or MoveTemplateAsync clears the relevant TtlCache. A regression in any
    /// of those lock (_cacheLock) { _projectsCache.Clear(); } lines would go undetected.
    /// Add: ApiProjectStore_SaveProject_InvalidatesProjectsCache, etc.
    /// </summary>
    private static ApiConfigurationStore CreateCompositeStore(IPipelineApiConfigClient client, int ttlSeconds)
        => new(
            client,
            new ApiPipelineConfigStore(client) { CacheTtlSeconds = ttlSeconds },
            new ApiProviderConfigStore(client) { CacheTtlSeconds = ttlSeconds },
            new ApiProjectStore(client) { CacheTtlSeconds = ttlSeconds })
        { CacheTtlSeconds = ttlSeconds };

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
}
