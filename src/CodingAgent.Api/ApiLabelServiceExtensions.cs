using CodingAgent.Orchestration.Dispatch;
using CodingAgent.Pipeline.Interfaces;
using Microsoft.Extensions.Logging;

namespace CodingAgent.Api;

/// <summary>
/// DI extension methods for label services.
/// </summary>
public static class ApiLabelServiceExtensions
{
    private sealed class ApiLabelServicesRegisteredMarker { }

    /// <summary>
    /// Registers label services: <see cref="ILabelSwapService"/>.
    /// <see cref="ILabelService"/> is registered by <see cref="ApiTokenVendingServiceExtensions.AddApiTokenVending"/>
    /// via <see cref="TokenVendingServiceCollectionExtensions.AddTokenVendingCore"/>.
    /// Idempotent — safe to call multiple times; subsequent calls are no-ops.
    /// </summary>
    public static IServiceCollection AddApiLabelServices(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        // Structural enforcement: ensure ILabelService is registered before the
        // ILabelSwapService factory below attempts to resolve it.
        services.AddApiTokenVending();

        if (services.Any(sd => sd.ServiceType == typeof(ApiLabelServicesRegisteredMarker)))
            return services;
        services.AddSingleton(new ApiLabelServicesRegisteredMarker());

        // ── ILabelSwapService ─────────────────────────────────────────────────
        // Registered conditionally so the API degrades gracefully when ILabelService is unconfigured.
        // LabelSwapService is internal sealed — accessed here through the same assembly.
        services.AddSingleton<ILabelSwapService>(sp =>
            new LabelSwapService(
                sp.GetRequiredService<ILabelService>(),
                sp.GetRequiredService<ILoggerFactory>().CreateLogger<LabelSwapService>()));

        return services;
    }
}
