using CodingAgentWebUI.Orchestration.Dispatch;
using CodingAgentWebUI.Orchestration.Registry;
using CodingAgentWebUI.Pipeline.Interfaces;
using Serilog;

namespace CodingAgentWebUI;

public static partial class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers agent registry and job deduplication guard services.
    /// </summary>
    private static void RegisterAgentRegistry(IServiceCollection services)
    {
        services.AddSingleton(sp => new AgentRegistryService(Log.Logger));
        services.AddSingleton<IAgentRegistryService>(sp => sp.GetRequiredService<AgentRegistryService>());
        services.AddSingleton(sp => new JobDeduplicationGuardService(
            sp.GetRequiredService<IAgentRegistryService>(),
            Log.Logger));
        services.AddSingleton<IJobDeduplicationGuard>(sp =>
            sp.GetRequiredService<JobDeduplicationGuardService>());
    }
}
