using CodingAgentWebUI.Infrastructure.Persistence;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Services;
using Serilog;

namespace CodingAgentWebUI;

public static partial class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers pipeline lifecycle services: issue context writer, run history,
    /// lifecycle service, and brain sync.
    /// IPipelineRunHistoryService is registered by AddWorkDistribution (Postgres-backed).
    /// </summary>
    private static void RegisterPipelineLifecycle(IServiceCollection services)
    {
        services.AddSingleton<IOpenIssueContextWriter>(sp => new OpenIssueContextWriter(Log.Logger));

        // IPipelineRunHistoryService is not registered here — it is registered by AddWorkDistribution
        // via RegisterKubernetesMode → PostgresPipelineRunHistoryService. If AddWorkDistribution is
        // ever removed or conditionalized, GetRequiredService below will fail at runtime.
        services.AddSingleton(sp => new PipelineRunLifecycleService(
            sp.GetRequiredService<IPipelineRunHistoryService>(),
            sp.GetRequiredService<IOrchestratorRunService>(),
            Log.Logger,
            sp.GetService<IAgentCancellationSender>()));
        services.AddSingleton<ILifecycleShutdownAction>(sp =>
            sp.GetRequiredService<PipelineRunLifecycleService>());

        services.AddSingleton<IBrainSyncService>(sp => new BrainSyncService(
            sp.GetRequiredService<IBrainUpdateService>(), Log.Logger));
    }
}
