using CodingAgent.AgentGateway;
using CodingAgent.Orchestration;
using CodingAgent.Orchestration.Dispatch;
using CodingAgent.Orchestration.Health;
using CodingAgent.Orchestration.Registry;
using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Services;
using CodingAgent.Web.Services;
using Microsoft.AspNetCore.SignalR;
using Serilog;

namespace CodingAgent.Web;

public static partial class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers job dispatching services: resolution services, dispatch infrastructure,
    /// agent communication, cancellation sender, model fetch, and the job dispatcher.
    /// </summary>
    private static void RegisterJobDispatching(IServiceCollection services)
    {
        services.AddDispatchResolutionServices(includeWorkItemClient: false);

        services.AddSingleton<IAgentCommunication>(sp => new SignalRAgentCommunication(
            sp.GetRequiredService<IHubContext<AgentHub, IAgentHubClient>>()));

        services.AddSingleton<IAgentCancellationSender>(sp => new AgentCancellationSender(
            sp.GetRequiredService<IAgentRegistryService>(),
            sp.GetRequiredService<IAgentCommunication>(),
            Log.Logger));

        services.AddSingleton<ModelFetchService>(sp => new ModelFetchService(
            sp.GetRequiredService<IAgentRegistryService>(),
            sp.GetRequiredService<IAgentCommunication>(),
            Log.Logger));
    }
}
