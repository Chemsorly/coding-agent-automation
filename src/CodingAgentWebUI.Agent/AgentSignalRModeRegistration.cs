using CodingAgentWebUI.Infrastructure.Resilience;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using KiroCliLib.Core;
using Microsoft.AspNetCore.SignalR.Client;
using ILogger = Serilog.ILogger;

namespace CodingAgentWebUI.Agent;

/// <summary>
/// Extension methods for registering SignalR-mode agent services.
/// Extracted from Program.cs to reduce top-level statement complexity.
/// </summary>
internal static class AgentSignalRModeRegistration
{
    internal static IServiceCollection AddSignalRModeServices(
        this IServiceCollection services,
        ILogger logger)
    {
        services.AddSingleton<CriticalMessageBuffer>();
        services.AddSingleton<SignalRCompletionReporter>(sp => new SignalRCompletionReporter(
            sp.GetRequiredService<HubConnectionManager>(),
            ResiliencePipelineFactory.CreateSignalRPipeline(logger),
            sp.GetRequiredService<CriticalMessageBuffer>(),
            logger));
        services.AddSingleton<IJobCompletionReporter>(sp => sp.GetRequiredService<SignalRCompletionReporter>());
        services.AddSingleton<AgentJobSlotManager>(sp =>
        {
            // Use lazy resolution to break the circular dependency:
            // AgentJobSlotManager -> AgentConnectionLifecycle -> AgentJobSlotManager.
            // The signalReady callback is only invoked at runtime (after DI construction),
            // so lazy resolution is safe here.
            var agentId = sp.GetRequiredService<AgentId>().Value;
            return new AgentJobSlotManager(async () =>
            {
                try
                {
                    var connectionLifecycle = sp.GetRequiredService<AgentConnectionLifecycle>();
                    await connectionLifecycle.Connection.InvokeAsync(
                        HubMethodNames.AgentReady, agentId);
                }
                catch (Exception ex)
                {
                    logger.Warning(ex, "Failed to send AgentReady signal");
                }
            });
        });
        services.AddSingleton<AgentConnectionLifecycle>(sp => new AgentConnectionLifecycle(
            sp.GetRequiredService<HubConnectionManager>(),
            sp.GetRequiredService<HubConnectionManagerFactory>(),
            sp.GetRequiredService<SignalRCompletionReporter>(),
            sp.GetRequiredService<AgentJobSlotManager>(),
            sp.GetRequiredService<AgentId>(),
            sp.GetRequiredService<IHostApplicationLifetime>(),
            logger));
        services.AddSingleton(sp => new AgentWorkerService(new AgentWorkerServiceDependencies(
            sp.GetRequiredService<AgentConnectionLifecycle>(),
            sp.GetRequiredService<AgentJobSlotManager>(),
            sp.GetRequiredService<AgentId>(),
            sp.GetRequiredService<IPipelineExecutor>(),
            sp.GetRequiredService<IConsolidationExecutor>(),
            sp.GetRequiredService<IJobCompletionReporter>(),
            sp.GetRequiredService<IKiroCliOrchestrator>(),
            sp.GetRequiredService<IHttpClientFactory>(),
            sp.GetRequiredService<IHostApplicationLifetime>(),
            logger)));
        services.AddHostedService(sp => sp.GetRequiredService<AgentWorkerService>());
        services.AddSingleton<IAgentService>(sp => sp.GetRequiredService<AgentWorkerService>());

        return services;
    }
}
