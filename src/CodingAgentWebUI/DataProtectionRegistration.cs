using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Serilog;
using StackExchange.Redis;

namespace CodingAgentWebUI;

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
    internal const string ApplicationName = "coding-agent-webui";

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
