using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Services;
using CodingAgent.Web.Services;
using Serilog;

namespace CodingAgent.Web;

public static partial class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the readiness drain service.
    /// </summary>
    private static void RegisterPipelineShutdown(IServiceCollection services)
    {
        // Readiness drain: marks /readyz as 503 during shutdown, then waits for endpoint removal.
        // The delay comes from READINESS_DRAIN_DELAY_SECONDS (default 15 s, clamped to 0–120 s),
        // read by ReadinessDrainService.ResolveDrainDelay.
        services.AddSingleton<ReadinessState>();
        services.AddHostedService(sp => new ReadinessDrainService(
            sp.GetRequiredService<ReadinessState>(),
            Log.Logger));
    }
}
