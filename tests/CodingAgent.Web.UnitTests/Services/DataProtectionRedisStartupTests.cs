using AwesomeAssertions;
using CodingAgent.Web;
using Microsoft.Extensions.DependencyInjection;
using StackExchange.Redis;

namespace CodingAgent.Web.UnitTests.Services;

/// <summary>
/// Tests for <see cref="DataProtectionRegistration.CreateMultiplexerFactory"/>.
///
/// Verifies that:
/// <list type="bullet">
///   <item>The factory returns <c>null</c> for null or empty connection strings (Redis not configured).</item>
///   <item>For a real (unreachable) endpoint, the factory returns a non-null, disconnected multiplexer
///         without throwing — proving <c>AbortOnConnectFail = false</c> is in effect.</item>
///   <item>Passing the factory to <see cref="DataProtectionRegistration.AddDataProtectionServices"/>
///         does not throw even when Redis is unreachable.</item>
/// </list>
///
/// <para>
/// Root cause of issue #3454: <c>Program.cs</c> passed a raw-string factory to
/// <c>AddDataProtectionServices</c>, which invokes <c>connectionMultiplexerFactory()</c>
/// immediately during registration (before <c>Build()</c>). With no <c>AbortOnConnectFail = false</c>,
/// the pod crashed with <c>RedisConnectionException</c> when Redis was unreachable at startup.
/// </para>
///
/// <para>
/// The last two tests make a real (attempted) connection to <c>127.0.0.1:1</c> with a 500 ms
/// timeout, so they will each take ~500 ms to complete. This is intentional.
/// </para>
/// </summary>
public class DataProtectionRedisStartupTests
{
    // ── CreateMultiplexerFactory — null / empty ───────────────────────────────

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void CreateMultiplexerFactory_WithoutConnectionString_ReturnsNull(string? connectionString)
    {
        // Act
        var factory = DataProtectionRegistration.CreateMultiplexerFactory(connectionString);

        // Assert
        factory.Should().BeNull(
            "no factory should be created when Redis is not configured — Data Protection falls back to the ephemeral in-process key ring");
    }

    // ── CreateMultiplexerFactory — unreachable Redis ───────────────────────────

    [Fact]
    public void CreateMultiplexerFactory_WithUnreachableRedis_ReturnsDisconnectedMultiplexer()
    {
        // Act — invoke the factory; this attempts a real connection to 127.0.0.1:1
        var factory = DataProtectionRegistration.CreateMultiplexerFactory("127.0.0.1:1,connectTimeout=500");
        factory.Should().NotBeNull("a factory must be returned when a connection string is provided");

        IConnectionMultiplexer? multiplexer = null;
        var act = () => { multiplexer = factory!(); };

        // Assert — must not throw; AbortOnConnectFail=false returns a disconnected multiplexer
        act.Should().NotThrow(
            "invoking the factory must not throw when Redis is unreachable — AbortOnConnectFail=false must be in effect");

        multiplexer.Should().NotBeNull("the factory must return a non-null multiplexer");
        multiplexer!.IsConnected.Should().BeFalse(
            "the multiplexer must start disconnected when Redis is unreachable — it will reconnect in the background");

        // Cleanup
        multiplexer.Dispose();
    }

    // ── AddDataProtectionServices — unreachable Redis ─────────────────────────

    [Fact]
    public void AddDataProtectionServices_WithUnreachableRedis_DoesNotThrow()
    {
        // Arrange — CreateMultiplexerFactory returns a factory that connects with AbortOnConnectFail=false.
        // AddDataProtectionServices invokes the factory immediately during registration (before Build()).
        var factory = DataProtectionRegistration.CreateMultiplexerFactory("127.0.0.1:1,connectTimeout=500");
        factory.Should().NotBeNull("a factory must be returned when a connection string is provided");

        var services = new ServiceCollection();
        services.AddLogging();

        // Act — must not throw even though Redis is unreachable when the factory is invoked
        var act = () => services.AddDataProtectionServices(factory);

        act.Should().NotThrow(
            "AddDataProtectionServices must not throw when Redis is unreachable — the multiplexer starts disconnected and reconnects in the background");

        // TODO: The IConnectionMultiplexer created inside AddDataProtectionServices (stored in the local
        // variable 'mux' captured by the PersistKeysToStackExchangeRedis lambda) is never disposed here.
        // The ServiceProvider is not built, so DI never calls Dispose on it. Background reconnect threads
        // spawned by StackExchange.Redis will outlive this test. It is not possible to assert IsConnected==false
        // on that internal multiplexer from here; see CreateMultiplexerFactory_WithUnreachableRedis_ReturnsDisconnectedMultiplexer
        // for the path that verifies the disconnected-multiplexer postcondition. Consider refactoring
        // AddDataProtectionServices to expose the multiplexer via DI so tests can resolve and dispose it.
        // (Review finding: DotNetSpecialist/TestQualityReviewer WARNING DataProtectionRedisStartupTests.cs:88)
    }
}
