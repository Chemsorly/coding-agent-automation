using Microsoft.Extensions.Configuration;
using Serilog;

namespace CodingAgent.Api;

/// <summary>
/// DI composition root for CodingAgent.Api.
/// Coordinates the registration of all API-layer services by delegating to
/// focused extension classes, each responsible for a single concern.
/// </summary>
public static class ApiServiceCollectionExtensions
{
    /// <summary>
    /// Registers all orchestration services needed by the Pipeline API hub graph:
    /// orchestration core, token vending, label services, lifecycle/consolidation,
    /// Kubernetes, dispatch, and chat dispatch.
    ///
    /// Each sub-registration is idempotent and structurally enforces its own
    /// prerequisites — no call-ordering comments are required here.
    /// </summary>
    public static IServiceCollection AddApiOrchestration(this IServiceCollection services, IConfiguration config)
    {
        // Serilog.ILogger for DI resolution (some services take Serilog.ILogger directly)
        services.AddSingleton(Log.Logger);

        ApiOrchestrationCoreServiceExtensions.AddOrchestrationCore(services, config);
        services.AddApiTokenVending();
        services.AddApiLabelServices();
        services.AddApiLifecycleAndConsolidation();
        services.AddApiKubernetes();
        services.AddApiDispatch();
        services.AddApiChatDispatch();

        return services;
    }
}
