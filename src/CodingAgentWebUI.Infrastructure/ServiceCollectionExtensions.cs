using CodingAgentWebUI.Infrastructure.Git;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Services;
using Microsoft.Extensions.DependencyInjection;

namespace CodingAgentWebUI.Infrastructure;

/// <summary>
/// Extension methods for registering shared pipeline services in the DI container.
/// Used by both WebUI and Agent entry points to avoid duplicating identical registrations.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers shared pipeline services that are identically configured in both
    /// the WebUI and Agent entry points.
    /// <para>
    /// Does NOT register <c>IProviderFactory</c> — the Agent uses <c>AgentProviderFactory</c>
    /// (with <c>IKiroCliOrchestrator</c> dependency) while WebUI uses <c>ProviderFactory</c>.
    /// Each entry point registers its own factory.
    /// </para>
    /// </summary>
    /// <param name="services">The service collection to add services to.</param>
    /// <param name="logger">The Serilog logger instance for service construction.</param>
    /// <returns>The service collection for chaining.</returns>
    public static IServiceCollection AddPipelineServices(this IServiceCollection services, Serilog.ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(logger);

        // IQualityGateValidator is consumed by IQualityGateExecutor (singleton).
        // Register as singleton to avoid captive dependency.
        services.AddSingleton<IQualityGateValidator>(sp => new QualityGateValidator(logger));

        services.AddSingleton<IBrainUpdateService>(sp => new BrainUpdateService(logger));

        services.AddSingleton<IAgentPhaseExecutor>(sp => new AgentPhaseExecutor(logger));

        services.AddSingleton<FeedbackService>(sp => new FeedbackService(logger));
        services.AddSingleton<PullRequestFinalizationService>(sp => new PullRequestFinalizationService(logger));
        services.AddSingleton<CiLogWriter>(sp => new CiLogWriter(logger));

        services.AddSingleton<IQualityGateExecutor>(sp => new QualityGateExecutor(
            sp.GetRequiredService<IQualityGateValidator>(),
            new PullRequestOrchestrator(logger),
            sp.GetRequiredService<CiLogWriter>(),
            sp.GetRequiredService<FeedbackService>(),
            logger,
            sp.GetRequiredService<IPipelineRunHistoryService>()));

        return services;
    }
}
