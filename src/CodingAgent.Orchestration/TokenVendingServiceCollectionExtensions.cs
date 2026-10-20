using CodingAgent.Pipeline.Interfaces;
using Microsoft.Extensions.DependencyInjection;
using Serilog;

namespace CodingAgent.Orchestration;

/// <summary>
/// Shared DI registration for the token-vending and label services:
/// <see cref="TokenVendingService"/> (concrete singleton),
/// <see cref="ITokenVendingService"/> (alias),
/// <see cref="TokenCacheHousekeepingService"/> (hosted service),
/// and <see cref="ILabelService"/>.
/// Used by both the API and the Scheduler to avoid duplicating identical
/// registration blocks across processes.
/// Requires a named HttpClient "TokenVending", <see cref="CodingAgent.Contracts.Interfaces.IProviderConfigStore"/>,
/// and <see cref="CodingAgent.Contracts.Interfaces.IProviderFactory"/> to already be registered.
/// </summary>
public static class TokenVendingServiceCollectionExtensions
{
    /// <summary>
    /// Registers the token-vending and label core services as singletons.
    /// </summary>
    public static IServiceCollection AddTokenVendingCore(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddSingleton<TokenVendingService>(sp =>
            new TokenVendingService(Log.Logger, sp.GetRequiredService<IHttpClientFactory>()));
        services.AddSingleton<ITokenVendingService>(sp => sp.GetRequiredService<TokenVendingService>());
        services.AddHostedService(sp => new TokenCacheHousekeepingService(
            sp.GetRequiredService<TokenVendingService>(), Log.Logger));
        services.AddSingleton<ILabelService>(sp => new LabelService(
            sp.GetRequiredService<IProviderConfigStore>(),
            sp.GetRequiredService<IProviderFactory>(),
            Log.Logger));
        return services;
    }
}
