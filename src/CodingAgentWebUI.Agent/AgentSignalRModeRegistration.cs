using CodingAgentWebUI.Infrastructure.Resilience;
using CodingAgentWebUI.Pipeline;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using KiroCliLib.Core;
using Microsoft.AspNetCore.SignalR.Client;
using ILogger = Serilog.ILogger;

namespace CodingAgentWebUI.Agent;

/// <summary>
/// Extension methods for registering chat-pod agent services.
/// Reached when the agent pod is started without <c>--work-item-id</c> (chat mode).
/// Registers <see cref="AgentWorkerService"/> and the full SignalR hub connection stack
/// (<see cref="AgentConnectionLifecycle"/>, <see cref="AgentJobSlotManager"/>,
/// <see cref="ChatJobHandler"/>, <see cref="ConsolidationJobHandler"/>,
/// <see cref="SignalRCompletionReporter"/>, <see cref="CriticalMessageBuffer"/>)
/// so the pod can serve interactive chat sessions and consolidation jobs.
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
                    var lifetime = sp.GetRequiredService<IHostApplicationLifetime>();
                    await connectionLifecycle.Connection.InvokeAsync(
                        HubMethodNames.AgentReady, agentId, lifetime.ApplicationStopping);
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
            return new ChatJobHandler(new ChatJobHandlerDependencies(
                sp.GetRequiredService<AgentConnectionLifecycle>(),
                sp.GetRequiredService<AgentJobSlotManager>(),
                sp.GetRequiredService<IKiroCliOrchestrator>(),
                sp.GetRequiredService<IHttpClientFactory>(),
                sp.GetRequiredService<IHostApplicationLifetime>(),
                SignalAgentReady: async () =>
                {
                    try
                    {
                        // TODO: [WARNING] AgentConnectionLifecycle and IHostApplicationLifetime are re-resolved
                        // from the DI container on every invocation of this delegate. Since both are registered
                        // as singletons this is safe today, but the pattern is inconsistent with the outer scope
                        // (which captures the resolved instances via the outer `sp`). If either registration were
                        // changed to scoped, the delegate would silently capture a different instance than
                        // ChatJobHandler's own _connectionLifecycle field. Prefer capturing the singleton
                        // instances from the outer factory scope rather than re-resolving on each call.
                        var lifecycle = sp.GetRequiredService<AgentConnectionLifecycle>();
                        var lifetime = sp.GetRequiredService<IHostApplicationLifetime>();
                        await lifecycle.Connection.InvokeAsync(HubMethodNames.AgentReady, agentId, lifetime.ApplicationStopping);
                    }
                    catch (Exception ex)
                    {
                        logger.Warning(ex, "Failed to send AgentReady signal from ChatJobHandler");
                    }
                },
                IsOpenCodeProvider: isOpenCodeProvider,
                IsChatMode: isChatMode,
                Logger: logger));
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
