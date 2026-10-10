using CodingAgent.Orchestration.Dispatch;

namespace CodingAgent.Web;

public static partial class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers job dispatching services: the dispatch resolution stack.
    /// </summary>
    private static void RegisterJobDispatching(IServiceCollection services)
    {
        services.AddDispatchResolutionServices(includeWorkItemClient: false);
    }
}
