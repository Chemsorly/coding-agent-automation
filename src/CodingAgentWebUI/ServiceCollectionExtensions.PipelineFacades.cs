using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Services;
using Serilog;

namespace CodingAgentWebUI;

public static partial class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers pipeline facade services: cancellation facade,
    /// orchestration service, dispatch run creator, and change notifier.
    /// </summary>
    private static void RegisterPipelineFacades(IServiceCollection services)
    {
        services.AddSingleton<IPipelineCancellationFacade>(sp => new PipelineCancellationFacade(
            sp.GetRequiredService<IJobDeduplicationGuard>(),
            sp.GetRequiredService<IAgentCancellationSender>()));

        services.AddSingleton(sp => new PipelineOrchestrationService(
            sp.GetRequiredService<IConfigurationStore>(),
            sp.GetRequiredService<IProviderFactory>(),
            sp.GetRequiredService<IPipelineCancellationFacade>(),
            sp.GetRequiredService<PipelineRunLifecycleService>(),
            sp.GetRequiredService<ILabelService>(),
            Log.Logger));
        services.AddSingleton<IOrchestrationShutdownAction>(sp =>
            sp.GetRequiredService<PipelineOrchestrationService>());
        services.AddSingleton<IPipelineOrchestrationService>(sp =>
            sp.GetRequiredService<PipelineOrchestrationService>());

        // Concrete-first registration: container owns the instance and calls DisposeAsync on shutdown.
        services.AddSingleton(sp =>
            new DispatchRunCreationService(
                sp.GetRequiredService<PipelineRunLifecycleService>(),
                sp.GetRequiredService<IProviderConfigStore>(),
                sp.GetRequiredService<IProviderFactory>(),
                Log.Logger));
        services.AddSingleton<IDispatchRunCreator>(sp => sp.GetRequiredService<DispatchRunCreationService>());
        services.AddSingleton<IChangeNotifier>(sp =>
            sp.GetRequiredService<PipelineRunLifecycleService>());
        services.AddSingleton<IChatNotifier>(sp =>
            sp.GetRequiredService<PipelineRunLifecycleService>());
    }
}
