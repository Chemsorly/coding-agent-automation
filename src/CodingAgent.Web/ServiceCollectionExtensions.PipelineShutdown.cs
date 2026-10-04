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
        services.AddSingleton<ReadinessState>();
        services.AddHostedService(sp =>
        {
            var opts = sp.GetService<Microsoft.Extensions.Options.IOptions<MonolithRuntimeOptions>>()?.Value;
            var drainDelay = opts is not null
                ? TimeSpan.FromSeconds(System.Math.Clamp(opts.ReadinessDrainDelaySeconds, 0, 120))
                : (TimeSpan?)null;
            return new ReadinessDrainService(
                sp.GetRequiredService<ReadinessState>(),
                Log.Logger,
                drainDelay: drainDelay);
        });
    }
}
