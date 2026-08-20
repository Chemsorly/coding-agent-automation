using AwesomeAssertions;
using CodingAgentWebUI.Api.Client;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Services;
using Moq;

namespace CodingAgentWebUI.UnitTests.Services;

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

    /// <summary>
    /// Builds the composite store the way DI does: over the same three narrow stores, so the
    /// caches under test are the ones production shares rather than private copies.
    /// </summary>
    private static ApiConfigurationStore CreateCompositeStore(IPipelineApiConfigClient client, int ttlSeconds)
        => new(
            client,
            new ApiPipelineConfigStore(client) { CacheTtlSeconds = ttlSeconds },
            new ApiProviderConfigStore(client) { CacheTtlSeconds = ttlSeconds },
            new ApiProjectStore(client) { CacheTtlSeconds = ttlSeconds })
        { CacheTtlSeconds = ttlSeconds };
}
