using CodingAgent.Orchestration.Redis;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Serilog;
using StackExchange.Redis;

namespace CodingAgent.Web;

/// <summary>
/// Registers ASP.NET Core Data Protection with a shared Redis key ring when Redis is configured.
///
/// <para>
/// Without a shared key ring, each orchestrator pod generates its own ephemeral keys in
/// <c>/home/ubuntu/.aspnet/DataProtection-Keys</c>. The Rancher proxy can load-balance the
/// initial page request to replica A (antiforgery token encrypted with A's key) then route
/// the Blazor WebSocket to replica B, which cannot decrypt the token — causing
/// <c>CryptographicException: The key was not found in the key ring</c> and the client
/// circuit-failure "The circuit failed to initialize".
/// </para>
///
/// <para>
/// When <paramref name="redisConnectionString"/> is provided, keys are persisted to Redis under
/// <c>caa:data-protection-keys</c> and all replicas share one ring. When absent (local dev /
/// single-replica), the default ephemeral in-process ring is used.
/// </para>
/// </summary>
public static class DataProtectionRegistration
{
    internal const string RedisKey = "caa:data-protection-keys";
    internal const string ApplicationName = "coding-agent-web";

    /// <summary>
    /// Creates a factory that returns an <see cref="IConnectionMultiplexer"/> for the given
    /// <paramref name="redisConnectionString"/>, or <c>null</c> if the string is null or empty
    /// (Redis not configured).
    ///
    /// <para>
    /// The factory defers the actual connection until it is invoked, but the caller
    /// (<see cref="AddDataProtectionServices"/>) invokes it immediately during service registration.
    /// <see cref="RedisConnectionOptions.Parse"/> ensures <c>AbortOnConnectFail = false</c>
    /// so that the connection returns a disconnected multiplexer instead of throwing when Redis
    /// is unreachable at startup.
    /// </para>
    /// </summary>
    /// <param name="redisConnectionString">Redis connection string, or <c>null</c> / <c>""</c> when Redis is not configured.</param>
    /// <returns>A factory delegate, or <c>null</c> when <paramref name="redisConnectionString"/> is null or empty.</returns>
    internal static Func<IConnectionMultiplexer>? CreateMultiplexerFactory(string? redisConnectionString)
    {
        if (string.IsNullOrEmpty(redisConnectionString))
            return null;
        return () => ConnectionMultiplexer.Connect(RedisConnectionOptions.Parse(redisConnectionString));
    }

    /// <summary>
    /// Configures Data Protection. When <paramref name="connectionMultiplexerFactory"/> is provided
    /// (i.e. Redis is configured), keys are persisted to Redis. Otherwise the default ephemeral
    /// in-process key ring is used.
    /// </summary>
    /// <param name="services">The service collection to configure.</param>
    /// <param name="connectionMultiplexerFactory">
    /// Factory that returns the <see cref="IConnectionMultiplexer"/> to use for key persistence.
    /// Pass <c>null</c> to fall back to the default ephemeral key ring.
    /// </param>
    internal static IServiceCollection AddDataProtectionServices(
        this IServiceCollection services,
        Func<IConnectionMultiplexer>? connectionMultiplexerFactory)
    {
        if (connectionMultiplexerFactory is null)
        {
            Log.Warning(
                "Data Protection: Redis not configured — " +
                "using ephemeral in-process key ring (single replica only)");
            return services;
        }

        var mux = connectionMultiplexerFactory();
        services.AddDataProtection()
            .PersistKeysToStackExchangeRedis(() => mux.GetDatabase(), RedisKey)
            .SetApplicationName(ApplicationName);

        Log.Information(
            "Data Protection: keys persisted to Redis (key={RedisKey})", RedisKey);

        return services;
    }
}
