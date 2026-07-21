using CodingAgentWebUI.Infrastructure;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Services;
using CodingAgentWebUI.Services;
using Serilog;

namespace CodingAgentWebUI;

public static partial class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers pipeline background services: orphaned label recovery, dependency checker,
    /// pipeline loop service, loop state persistence, and issue description parser.
    /// </summary>
    private static void RegisterPipelineBackgroundServices(IServiceCollection services)
    {
        services.AddHostedService(sp => new OrphanedLabelRecoveryService(
            sp.GetRequiredService<IOrchestratorRunService>(),
            sp.GetRequiredService<IProjectStore>(),
            sp.GetRequiredService<IProviderConfigStore>(),
            sp.GetRequiredService<IProviderFactory>(),
            sp.GetRequiredService<ILabelService>(),
            sp.GetRequiredService<IPipelineConfigStore>(),
            Log.Logger));

        services.AddSingleton<IDependencyChecker>(sp => new DependencyChecker(Log.Logger));
        services.AddSingleton<PipelineLoopService>(sp => new PipelineLoopService(
            sp.GetRequiredService<IDispatchRunCreator>(),
            sp.GetRequiredService<IProviderFactory>(),
            sp.GetRequiredService<IPipelineConfigStore>(),
            sp.GetRequiredService<IProviderConfigStore>(),
            sp.GetRequiredService<IProjectStore>(),
            Log.Logger,
            sp.GetService<IWorkDistributor>(),
            sp.GetService<IDispatchOrchestrationService>(),
            sp.GetRequiredService<IDependencyChecker>()));
        services.AddSingleton<IPipelineLoopService>(sp => sp.GetRequiredService<PipelineLoopService>());
        services.AddHostedService(sp => sp.GetRequiredService<PipelineLoopService>());

        // Loop state persistence: auto-resumes loop after pod restart if previously active
        services.AddSingleton(sp => new LoopStatePersistenceService(
            sp.GetRequiredService<IPipelineLoopService>(),
            Log.Logger,
            sp.GetRequiredService<ILoopStateStore>()));
        services.AddHostedService(sp => sp.GetRequiredService<LoopStatePersistenceService>());

        services.AddTransient<IssueDescriptionParser>();
    }
}
