using CodingAgent.Infrastructure;
using CodingAgent.Orchestration;
using CodingAgent.Orchestration.Registry;
using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;
using CodingAgent.Pipeline.Services;
using CodingAgent.AgentGateway;
using CodingAgent.Web.Services;
using Serilog;

namespace CodingAgent.Web;

/// <summary>
/// Extension methods for registering domain services in the DI container.
/// Extracted from Program.cs to reduce file size and group related registrations.
/// </summary>
public static partial class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers infrastructure services. Config stores are registered by
    /// <see cref="AddWorkDistribution"/> and <c>RegisterPipelineBackgroundServices</c>.
    /// </summary>
    public static IServiceCollection AddInfrastructureServices(
        this IServiceCollection services)
    {
        // Only the label service still takes it: the drawers browse and dispatch through the API since
        // spec 051 2.3, and Settings' provider checks since 2.1c. Both go with the orchestration
        // services in 4.1.
        services.AddSingleton<IProviderFactory>(sp => new ProviderFactory(sp.GetRequiredService<IPipelineConfigStore>()));

        return services;
    }

    /// <summary>
    /// Registers WebUI-specific pipeline services: shutdown and background services.
    /// Config stores are registered by <see cref="AddWorkDistribution"/> and <c>RegisterPipelineBackgroundServices</c>.
    /// </summary>
    public static IServiceCollection AddPipelineCoreServices(this IServiceCollection services)
    {
        // ── Shutdown ───────────────────────────────────────────────────────
        RegisterPipelineShutdown(services);

        // ── Background Services ────────────────────────────────────────────
        RegisterPipelineBackgroundServices(services);

        return services;
    }

    /// <summary>
    /// Registers multi-agent orchestration services: agent registry and the label service.
    /// HeartbeatMonitorService was deleted at Spec 041–045 arc close — agent timeouts
    /// are enforced by ReconciliationService in JobController.
    /// </summary>
    public static IServiceCollection AddOrchestrationServices(this IServiceCollection services)
    {
        // ── Agent Registry ─────────────────────────────────────────────────
        RegisterAgentRegistry(services);

        // ── Label Service ──────────────────────────────────────────────────
        RegisterLabelService(services);

        // ── Background Services ────────────────────────────────────────────
        RegisterOrchestrationBackgroundServices(services);

        return services;
    }
}
