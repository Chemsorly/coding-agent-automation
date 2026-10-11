using CodingAgent.Orchestration;
using CodingAgent.Pipeline.Interfaces;
using Microsoft.Extensions.Http.Resilience;
using Serilog;

namespace CodingAgent.Web;

public static partial class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers token vending and the label service.
    /// </summary>
    private static void RegisterTokenAndRunServices(IServiceCollection services)
    {
        services.AddHttpClient("TokenVending")
            .AddStandardResilienceHandler(o => o.CircuitBreaker.MinimumThroughput = 10);
        services.AddSingleton<TokenVendingService>(sp =>
            new TokenVendingService(Log.Logger, sp.GetRequiredService<IHttpClientFactory>()));
        services.AddSingleton<ITokenVendingService>(sp => sp.GetRequiredService<TokenVendingService>());
        services.AddHostedService(sp => new TokenCacheHousekeepingService(sp.GetRequiredService<TokenVendingService>(), Log.Logger));

        services.AddSingleton<ILabelService>(sp => new LabelService(
            sp.GetRequiredService<IConfigurationStore>(),
            sp.GetRequiredService<IProviderFactory>(),
            Log.Logger));
    }
}
