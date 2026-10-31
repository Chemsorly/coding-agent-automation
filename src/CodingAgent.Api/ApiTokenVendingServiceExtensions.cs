using CodingAgent.Api.Client;
using CodingAgent.Infrastructure.Common;
using CodingAgent.Orchestration;

namespace CodingAgent.Api;

/// <summary>
/// DI extension methods for token-vending services.
/// </summary>
public static class ApiTokenVendingServiceExtensions
{
    private sealed class ApiTokenVendingRegisteredMarker { }

    /// <summary>
    /// Registers token-vending services: HTTP client defaults, named client, service, and
    /// the housekeeping hosted service.
    /// Idempotent — safe to call multiple times; subsequent calls are no-ops.
    /// </summary>
    public static IServiceCollection AddApiTokenVending(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        if (services.Any(sd => sd.ServiceType == typeof(ApiTokenVendingRegisteredMarker)))
            return services;
        services.AddSingleton(new ApiTokenVendingRegisteredMarker());

        // ── ITokenVendingService ─────────────────────────────────────────────
        // SocketsHttpHandler.PooledConnectionLifetime set to 90s via AddStandardHttpClientPoolDefaults.
        // CircuitBreaker.MinimumThroughput lowered to 10 so the breaker can trip on low-traffic
        // clients during a pod blip.
        services.AddStandardHttpClientPoolDefaults();
        services.AddHttpClient("TokenVending")
            .AddStandardResilienceHandler(
                PipelineApiClientServiceCollectionExtensions.ConfigureResilienceOptions);
        services.AddTokenVendingCore();

        return services;
    }
}
