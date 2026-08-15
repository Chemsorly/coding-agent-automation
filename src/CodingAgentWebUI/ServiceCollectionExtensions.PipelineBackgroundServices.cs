using CodingAgentWebUI.Infrastructure;
using CodingAgentWebUI.Orchestration.LeaderElection;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
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
        services.AddSingleton<IHousekeepingService>(sp => new HousekeepingService(
            sp.GetRequiredService<IOrchestratorRunService>(),
            Log.Logger));
        services.AddSingleton<PipelineLoopServiceDependencies>(sp => new PipelineLoopServiceDependencies
        {
            Orchestration = sp.GetRequiredService<IDispatchRunCreator>(),
            ProviderFactory = sp.GetRequiredService<IProviderFactory>(),
            PipelineConfigStore = sp.GetRequiredService<IPipelineConfigStore>(),
            ProviderConfigStore = sp.GetRequiredService<IProviderConfigStore>(),
            ProjectStore = sp.GetRequiredService<IProjectStore>(),
            Logger = Log.Logger,
            WorkDistributor = sp.GetService<IWorkDistributor>(),
            DispatchOrchestration = sp.GetService<IDispatchOrchestrationService>(),
            DependencyChecker = sp.GetRequiredService<IDependencyChecker>(),
            HousekeepingService = sp.GetRequiredService<IHousekeepingService>(),
            // GetService returns null when ILeaderElectionService is not registered (Legacy mode),
            // which causes the loop to run unconditionally as before. In K8s and SignalR+DB modes
            // ILeaderElectionService implements ILeaderGate and the loop is leader-gated.
            LeaderElection = sp.GetService<ILeaderElectionService>()
        });
        services.AddSingleton<PipelineLoopService>();
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
