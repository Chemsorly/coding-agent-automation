using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Services;
using CodingAgent.Web.Services;
using Serilog;

namespace CodingAgent.Web;

public static partial class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the change and chat notifiers.
    /// </summary>
    private static void RegisterPipelineFacades(IServiceCollection services)
    {
        // IChangeNotifier: NullChangeNotifier registered as a null-object for shared libraries
        // that declare an IChangeNotifier constructor dependency. The monolith no longer drives
        // state-change notifications directly — change events arrive via IAgentHubConnection
        // hub push events (OnStepTransition, OnRunCompleted). IChangeNotifier is still registered
        // with a real implementation in CodingAgent.AgentGateway for AgentHub internals.
        services.AddSingleton<IChangeNotifier, NullChangeNotifier>();
        services.AddSingleton<IChatNotifier>(sp =>
            sp.GetRequiredService<PipelineRunLifecycleService>());
    }
}
