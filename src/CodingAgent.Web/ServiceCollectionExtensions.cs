using CodingAgent.Pipeline.Services;
using Serilog;

namespace CodingAgent.Web;

/// <summary>Service registrations of the web host that do not fit a page or the UI clients.</summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Readiness drain: on shutdown <c>/readyz</c> turns 503 and the host waits
    /// <c>READINESS_DRAIN_DELAY_SECONDS</c> before it stops, so the load balancer stops sending new circuits first.
    /// </summary>
    public static IServiceCollection AddReadinessDrain(this IServiceCollection services)
    {
        services.AddSingleton<ReadinessState>();
        services.AddHostedService(sp => new ReadinessDrainService(sp.GetRequiredService<ReadinessState>(), Log.Logger));
        return services;
    }
}
