using AwesomeAssertions;
using CodingAgentWebUI;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Moq;
using StackExchange.Redis;

namespace CodingAgentWebUI.UnitTests.Services;

/// <summary>
/// Unit tests for <see cref="DataProtectionRegistration.AddDataProtectionServices"/>.
///
/// These tests verify the DI wiring shape — specifically whether keys are persisted
/// to Redis (multi-replica) or kept in the default ephemeral in-process ring (single-replica
/// / local dev). They do NOT connect to a real Redis instance.
///
/// Root cause documented: when 2+ orchestrator replicas run, each pod's ephemeral key ring
/// causes AntiforgeryValidationException ("key not found") when the Rancher proxy routes
/// the page load and the Blazor WebSocket to different pods, which manifests as
/// "The circuit failed to initialize" on the client.
/// </summary>
public class DataProtectionRegistrationTests
{
    // ── helpers ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Builds a service provider with AddDataProtectionServices wired and resolves
    /// the KeyManagementOptions so tests can inspect the configured IXmlRepository.
    /// </summary>
    private static KeyManagementOptions ResolveKeyManagementOptions(
        Func<IConnectionMultiplexer>? multiplexerFactory)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDataProtectionServices(multiplexerFactory);

        var provider = services.BuildServiceProvider();
        var opts = provider.GetRequiredService<IOptions<KeyManagementOptions>>();
        return opts.Value;
    }

    /// <summary>
    /// Creates a mock <see cref="IConnectionMultiplexer"/> whose <c>GetDatabase()</c>
    /// returns a mock <see cref="IDatabase"/>. Sufficient for DI-wiring tests — no
    /// network calls are made.
    /// </summary>
    private static IConnectionMultiplexer CreateMockMultiplexer()
    {
        var db = new Mock<IDatabase>();
        // PersistKeysToStackExchangeRedis stores XML; mock RedisValue reads so the
        // repository initialises without a real server.
        db.Setup(d => d.ListRange(It.IsAny<RedisKey>(), It.IsAny<long>(), It.IsAny<long>(), It.IsAny<CommandFlags>()))
          .Returns([]);
        var mux = new Mock<IConnectionMultiplexer>();
        mux.Setup(m => m.GetDatabase(It.IsAny<int>(), It.IsAny<object>())).Returns(db.Object);
        return mux.Object;
    }

    // ── no Redis (null factory) ───────────────────────────────────────────────

    [Fact]
    public void WhenNullFactory_XmlRepository_IsNull()
    {
        // When Redis is not configured, AddDataProtection() is not called at all,
        // so KeyManagementOptions.XmlRepository stays at the default (null — framework picks ephemeral).
        var opts = ResolveKeyManagementOptions(multiplexerFactory: null);

        opts.XmlRepository.Should().BeNull(
            "no Redis configured — framework falls back to ephemeral in-process key ring");
    }

    [Fact]
    public void WhenNullFactory_IDataProtectionProvider_StillResolvable()
    {
        // Even without explicit AddDataProtection(), ASP.NET Core auto-registers a default provider.
        // Verify DI does not throw — the app can still start in single-replica / dev mode.
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDataProtection(); // ASP.NET Core default (no Redis)
        services.AddDataProtectionServices(connectionMultiplexerFactory: null);

        var act = () => services.BuildServiceProvider()
                                .GetRequiredService<IDataProtectionProvider>();

        act.Should().NotThrow("IDataProtectionProvider must resolve in fallback mode");
    }

    // ── with Redis (non-null factory) ────────────────────────────────────────

    [Fact]
    public void WhenFactoryProvided_XmlRepository_IsNotNull()
    {
        // PersistKeysToStackExchangeRedis sets KeyManagementOptions.XmlRepository to
        // a RedisXmlRepository instance. Asserting non-null verifies the wiring is active.
        var opts = ResolveKeyManagementOptions(() => CreateMockMultiplexer());

        opts.XmlRepository.Should().NotBeNull(
            "Redis is configured — keys must be stored in the shared Redis ring, not in-process");
    }

    [Fact]
    public void WhenFactoryProvided_XmlRepository_IsRedisXmlRepository()
    {
        var opts = ResolveKeyManagementOptions(() => CreateMockMultiplexer());

        opts.XmlRepository!.GetType().Name.Should().Be("RedisXmlRepository",
            "PersistKeysToStackExchangeRedis must register RedisXmlRepository, not a file or in-memory repository");
    }

    [Fact]
    public void WhenFactoryProvided_ApplicationName_IsCodingAgentWebui()
    {
        // SetApplicationName isolates the key ring per app — prevents key sharing accidents
        // with other apps pointing at the same Redis instance.
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDataProtectionServices(() => CreateMockMultiplexer());

        var provider = services.BuildServiceProvider();
        var dpOptions = provider.GetRequiredService<IOptions<DataProtectionOptions>>();

        dpOptions.Value.ApplicationDiscriminator.Should().Be(
            DataProtectionRegistration.ApplicationName,
            "application name must be scoped to 'coding-agent-webui' to prevent cross-app key sharing");
    }

    [Fact]
    public void WhenFactoryProvided_MultiplexerFactory_IsCalledExactlyOnce()
    {
        // The factory should be called once during DI registration, not per-resolve.
        var callCount = 0;
        var mux = CreateMockMultiplexer();
        Func<IConnectionMultiplexer> factory = () =>
        {
            callCount++;
            return mux;
        };

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDataProtectionServices(factory);
        _ = services.BuildServiceProvider(); // trigger any deferred factory calls

        callCount.Should().Be(1, "the multiplexer factory should be called exactly once during registration");
    }

    // ── constants ─────────────────────────────────────────────────────────────

    [Fact]
    public void RedisKey_ConstantValue_IsExpected()
    {
        // Pin the Redis key name — changing it would orphan all existing keys in production.
        DataProtectionRegistration.RedisKey.Should().Be("caa:data-protection-keys");
    }

    [Fact]
    public void ApplicationName_ConstantValue_IsExpected()
    {
        // Pin the application name — changing it invalidates all in-flight antiforgery tokens.
        DataProtectionRegistration.ApplicationName.Should().Be("coding-agent-webui");
    }
}
