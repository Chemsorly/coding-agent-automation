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
}
