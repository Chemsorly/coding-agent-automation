using CodingAgentWebUI.Api.Client;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Services;
using Microsoft.Extensions.DependencyInjection;
using Serilog;

namespace CodingAgentWebUI.Orchestration.Dispatch;

/// <summary>
/// Shared DI registration for the dispatch resolution stack:
/// <see cref="ProfileResolver"/>, <see cref="QualityGateResolver"/>,
/// <see cref="ReviewerResolver"/>, <see cref="DispatchResolutionService"/>,
/// and <see cref="DispatchInfrastructure"/>.
/// Used by both the Scheduler and the WebUI monolith to avoid duplicating
/// identical registration blocks across processes.
/// </summary>
public static class DispatchResolutionServiceCollectionExtensions
{
    /// <summary>
    /// Registers the dispatch resolution stack as singletons.
    /// Requires <see cref="ITokenVendingService"/>, <see cref="IProviderFactory"/>,
    /// <see cref="ILabelService"/>, and <see cref="IConfigurationStore"/> to already
    /// be registered.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="workItemClient">
    /// Optional: the <see cref="IPipelineApiWorkItemClient"/> to pass to
    /// <see cref="DispatchInfrastructure"/> for agent-error staleness checking.
    /// Pass <c>null</c> in contexts where the work-item API is unavailable
    /// (e.g., the WebUI monolith, which uses a locally disconnected hub).
    /// </param>
    public static IServiceCollection AddDispatchResolutionServices(
        this IServiceCollection services,
        bool includeWorkItemClient = false)
    {
        services.AddSingleton<ProfileResolver>();
        services.AddSingleton<QualityGateResolver>();
        services.AddSingleton<ReviewerResolver>();

        services.AddSingleton(sp => new DispatchResolutionService(
            sp.GetRequiredService<ProfileResolver>(),
            sp.GetRequiredService<QualityGateResolver>(),
            sp.GetRequiredService<ReviewerResolver>(),
            sp.GetRequiredService<Pipeline.Interfaces.IConfigurationStore>(),
            Log.Logger));

        services.AddSingleton(sp => new DispatchInfrastructure(
            sp.GetRequiredService<ITokenVendingService>(),
            sp.GetRequiredService<IProviderFactory>(),
            sp.GetRequiredService<ILabelService>(),
            sp.GetRequiredService<DispatchResolutionService>(),
            includeWorkItemClient ? sp.GetRequiredService<IPipelineApiWorkItemClient>() : null));

        return services;
    }
}
