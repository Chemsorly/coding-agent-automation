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
                    // TODO: Pass IHostApplicationLifetime.ApplicationStopping as the CancellationToken here.
                    // The original AgentWorkerService.SignalAgentReadyAsync() passed _hostApplicationLifetime.ApplicationStopping,
                    // which ensures the InvokeAsync is cancelled during shutdown rather than hanging until the connection drops.
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
        services.AddSingleton<ChatJobHandler>(sp =>
        {
            var agentId = sp.GetRequiredService<AgentId>().Value;
            var isOpenCodeProvider = (Environment.GetEnvironmentVariable(AgentDefaults.EnvAgentProviderType) ?? "")
                .Equals(AgentDefaults.OpenCodeHttpClientName, StringComparison.OrdinalIgnoreCase);
            var isChatMode = string.Equals(
                Environment.GetEnvironmentVariable(AgentDefaults.EnvChatMode), "true", StringComparison.OrdinalIgnoreCase);
            return new ChatJobHandler(
                sp.GetRequiredService<AgentConnectionLifecycle>(),
                sp.GetRequiredService<AgentJobSlotManager>(),
                sp.GetRequiredService<IKiroCliOrchestrator>(),
                sp.GetRequiredService<IHttpClientFactory>(),
                sp.GetRequiredService<IHostApplicationLifetime>(),
                signalAgentReady: async () =>
                {
                    try
                    {
                        // TODO: Inconsistency — agentId is captured as a primitive but lifecycle is re-resolved via the
                        // container on every invocation. Because AgentConnectionLifecycle is a singleton this is harmless,
                        // but it diverges from the pattern used in the AgentJobSlotManager lambda above (which also
                        // re-resolves). Consider capturing the already-resolved lifecycle directly to make the pattern
                        // consistent and resilient to future registration changes.
                        // TODO: Pass IHostApplicationLifetime.ApplicationStopping as the CancellationToken here, consistent
                        // with the original AgentWorkerService.SignalAgentReadyAsync(). Without it, a pending AgentReady
                        // invocation during shutdown may hang until the connection drops naturally.
                        var lifecycle = sp.GetRequiredService<AgentConnectionLifecycle>();
                        await lifecycle.Connection.InvokeAsync(HubMethodNames.AgentReady, agentId);
                    }
                    catch (Exception ex)
                    {
                        logger.Warning(ex, "Failed to send AgentReady signal from ChatJobHandler");
                    }
                },
                agentId: agentId,
                isOpenCodeProvider: isOpenCodeProvider,
                isChatMode: isChatMode,
                logger: logger);
        });
        services.AddSingleton<ConsolidationJobHandler>(sp => new ConsolidationJobHandler(
            sp.GetRequiredService<AgentConnectionLifecycle>(),
            sp.GetRequiredService<AgentJobSlotManager>(),
            sp.GetRequiredService<IConsolidationExecutor>(),
            logger));
        services.AddSingleton(sp => new AgentWorkerService(new AgentWorkerServiceDependencies(
            sp.GetRequiredService<AgentConnectionLifecycle>(),
            sp.GetRequiredService<AgentJobSlotManager>(),
            sp.GetRequiredService<ChatJobHandler>(),
            sp.GetRequiredService<ConsolidationJobHandler>(),
            sp.GetRequiredService<AgentId>(),
            sp.GetRequiredService<IPipelineExecutor>(),
            sp.GetRequiredService<IJobCompletionReporter>(),
            sp.GetRequiredService<IHostApplicationLifetime>(),
            logger)));
        services.AddHostedService(sp => sp.GetRequiredService<AgentWorkerService>());
        services.AddSingleton<IAgentService>(sp => sp.GetRequiredService<AgentWorkerService>());

        return services;
    }
}
