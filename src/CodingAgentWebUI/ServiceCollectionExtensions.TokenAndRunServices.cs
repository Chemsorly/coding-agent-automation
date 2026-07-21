using CodingAgentWebUI.Infrastructure;
using CodingAgentWebUI.Orchestration;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using Microsoft.Extensions.Http.Resilience;
using Serilog;

namespace CodingAgentWebUI;

public static partial class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers token vending, orchestrator run service, and label service.
    /// </summary>
    private static void RegisterTokenAndRunServices(IServiceCollection services, PipelineConfiguration pipelineConfig)
    {
        services.AddHttpClient("TokenVending")
            .AddStandardResilienceHandler();
        services.AddSingleton<ITokenVendingService>(sp => new TokenVendingService(Log.Logger, sp.GetRequiredService<IHttpClientFactory>()));

        services.AddSingleton(sp => new OrchestratorRunService(
            Log.Logger,
            pipelineConfig.OutputBufferCapacity));
        services.AddSingleton<IOrchestratorRunService>(sp => sp.GetRequiredService<OrchestratorRunService>());

        services.AddSingleton<ILabelService>(sp => new LabelService(
            sp.GetRequiredService<IConfigurationStore>(),
            sp.GetRequiredService<IProviderFactory>(),
            Log.Logger));
    }
}
