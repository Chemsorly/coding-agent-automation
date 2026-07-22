using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Services;
using CodingAgentWebUI.Services;
using Serilog;

namespace CodingAgentWebUI;

public static partial class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers shutdown infrastructure: shutdown signal, graceful shutdown service,
    /// and readiness drain service.
    /// </summary>
    private static void RegisterPipelineShutdown(IServiceCollection services)
    {
        // Shutdown signal: cooperative flag to prevent dispatch-during-shutdown races
        services.AddSingleton<IShutdownSignal>(new ShutdownSignal());

        // Graceful shutdown via IHostedLifecycleService (async, 15s timeout, non-blocking)
        services.AddHostedService(sp => new ShutdownService(
            sp.GetRequiredService<ILifecycleShutdownAction>(),
            sp.GetRequiredService<IOrchestrationShutdownAction>(),
            sp.GetRequiredService<IShutdownSignal>(),
            Log.Logger));

        // Readiness drain: marks /readyz as 503 during shutdown, then waits for endpoint removal.
        // Registered AFTER ShutdownService — StoppingAsync fires in REVERSE order,
        // so drain runs FIRST (flips readiness, waits), THEN ShutdownService cancels work.
        services.AddSingleton<ReadinessState>();
        services.AddHostedService(sp => new ReadinessDrainService(
            sp.GetRequiredService<ReadinessState>(),
            Log.Logger));
    }
}
