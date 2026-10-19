using Microsoft.Extensions.DependencyInjection;

namespace CodingAgent.Infrastructure.Common;

/// <summary>
/// Extension methods for HTTP client DI registration shared across hosts.
/// </summary>
public static class HttpClientExtensions
{
    /// <summary>
    /// Configures the default <see cref="System.Net.Http.SocketsHttpHandler"/> for all named HTTP
    /// clients in this <see cref="IServiceCollection"/>, setting
    /// <see cref="System.Net.Http.SocketsHttpHandler.PooledConnectionLifetime"/> to 90 seconds so
    /// stale connections to replaced pod IPs are recycled after a rolling update.
    /// </summary>
    /// <remarks>
    /// Idempotent — calling this method more than once on the same
    /// <see cref="IServiceCollection"/> is a no-op; only one
    /// <c>ConfigurePrimaryHttpMessageHandler</c> delegate is registered regardless of call count.
    /// </remarks>
    public static IServiceCollection AddStandardHttpClientPoolDefaults(
        this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        // Sentinel: prevent double-registration when multiple callers (e.g. AddTokenVending and
        // AddPipelineApiClient) both run in the same DI container. Without this guard, each call
        // stacks another ConfigurePrimaryHttpMessageHandler delegate and the previous
        // SocketsHttpHandler instance is created and immediately abandoned (minor leak until GC).
        if (services.Any(d => d.ServiceType == typeof(HttpClientPoolDefaultsSentinel)))
            return services;

        services.AddSingleton<HttpClientPoolDefaultsSentinel>();
        services.ConfigureHttpClientDefaults(b =>
            b.ConfigurePrimaryHttpMessageHandler(() => new System.Net.Http.SocketsHttpHandler
            {
                PooledConnectionLifetime = TimeSpan.FromSeconds(90)
            }));

        return services;
    }

    // Marker class — presence in the container indicates AddStandardHttpClientPoolDefaults has
    // already been called. Kept internal so InternalsVisibleTo grants test access without
    // polluting the public API surface.
    internal sealed class HttpClientPoolDefaultsSentinel { }
}
