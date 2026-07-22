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
    /// In DB mode, skips the in-memory history service (registered by AddWorkDistribution instead).
    /// </summary>
    private static void RegisterPipelineLifecycle(IServiceCollection services, bool isDatabaseMode)
    {
        services.AddSingleton<IOpenIssueContextWriter>(sp => new OpenIssueContextWriter(Log.Logger));

        if (!isDatabaseMode)
        {
            services.AddSingleton<IPipelineRunHistoryService>(sp => new PipelineRunHistoryService(Log.Logger));
        }

        // TODO: In DB mode (isDatabaseMode: true), IPipelineRunHistoryService is not registered here —
        // it depends on AddWorkDistribution being called separately. If AddWorkDistribution is ever
        // removed or conditionalized, this GetRequiredService call will fail at runtime.
        services.AddSingleton(sp => new PipelineRunLifecycleService(
            sp.GetRequiredService<IPipelineRunHistoryService>(),
            sp.GetRequiredService<IOrchestratorRunService>(),
            Log.Logger));
        services.AddSingleton<ILifecycleShutdownAction>(sp =>
            sp.GetRequiredService<PipelineRunLifecycleService>());

        services.AddSingleton<IBrainSyncService>(sp => new BrainSyncService(
            sp.GetRequiredService<IBrainUpdateService>(), Log.Logger));
    }
}
