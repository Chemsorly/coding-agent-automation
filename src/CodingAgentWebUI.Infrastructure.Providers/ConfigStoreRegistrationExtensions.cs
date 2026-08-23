using CodingAgentWebUI.Pipeline.Interfaces;
using Microsoft.Extensions.DependencyInjection;

namespace CodingAgentWebUI.Infrastructure;

/// <summary>
/// Extension methods for registering IConfigurationStore sub-interface forwarding.
/// Both the monolith and the API call this after registering IConfigurationStore itself.
/// </summary>
public static class ConfigStoreRegistrationExtensions
{
    /// <summary>
    /// Registers all IConfigurationStore sub-interface forwarding registrations.
    /// Ensures all consumers can resolve typed stores from the single IConfigurationStore singleton.
    /// MUST be called AFTER IConfigurationStore itself is registered.
    /// </summary>
    public static IServiceCollection RegisterConfigStoreSubInterfaces(this IServiceCollection services)
    {
        services.AddSingleton<IPipelineConfigStore>(sp => sp.GetRequiredService<IConfigurationStore>());
        services.AddSingleton<IProviderConfigStore>(sp => sp.GetRequiredService<IConfigurationStore>());
        services.AddSingleton<IAgentProfileStore>(sp => sp.GetRequiredService<IConfigurationStore>());
        services.AddSingleton<IQualityGateConfigStore>(sp => sp.GetRequiredService<IConfigurationStore>());
        services.AddSingleton<IReviewerConfigStore>(sp => sp.GetRequiredService<IConfigurationStore>());
        services.AddSingleton<IProjectStore>(sp => sp.GetRequiredService<IConfigurationStore>());
        return services;
    }
}
