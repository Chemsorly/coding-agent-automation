using CodingAgentWebUI.Hub;
using CodingAgentWebUI.Orchestration;
using CodingAgentWebUI.Orchestration.Dispatch;
using CodingAgentWebUI.Orchestration.Health;
using CodingAgentWebUI.Orchestration.Registry;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Services;
using CodingAgentWebUI.Services;
using Microsoft.AspNetCore.SignalR;
using Serilog;

namespace CodingAgentWebUI;

public static partial class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers job dispatching services: resolution services, dispatch infrastructure,
    /// agent communication, cancellation sender, model fetch, and the job dispatcher.
    /// </summary>
    private static void RegisterJobDispatching(IServiceCollection services)
    {
        services.AddSingleton<ProfileResolver>();
        services.AddSingleton<QualityGateResolver>();
        services.AddSingleton<ReviewerResolver>();

        services.AddSingleton(sp => new DispatchResolutionService(
            sp.GetRequiredService<ProfileResolver>(),
            sp.GetRequiredService<QualityGateResolver>(),
            sp.GetRequiredService<ReviewerResolver>(),
            sp.GetRequiredService<IConfigurationStore>(),
            Log.Logger));

        services.AddSingleton(sp => new DispatchInfrastructure(
            sp.GetRequiredService<ITokenVendingService>(),
            sp.GetRequiredService<IProviderFactory>(),
            sp.GetRequiredService<ILabelService>(),
            sp.GetRequiredService<DispatchResolutionService>(),
        sp.GetService<AnalysisStalenessDetector>()));  // null — not registered in monolith DI

        services.AddSingleton<IAgentCommunication>(sp => new SignalRAgentCommunication(
            sp.GetRequiredService<IHubContext<AgentHub, IAgentHubClient>>()));

        services.AddSingleton<IAgentCancellationSender>(sp => new AgentCancellationSender(
            sp.GetRequiredService<IAgentRegistryService>(),
            sp.GetRequiredService<IAgentCommunication>(),
            Log.Logger));

        services.AddSingleton<ModelFetchService>(sp => new ModelFetchService(
            sp.GetRequiredService<AgentRegistryService>(),
            sp.GetRequiredService<IAgentCommunication>(),
            Log.Logger));
    }
}
