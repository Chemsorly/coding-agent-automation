using CodingAgent.Infrastructure.Resilience;
using CodingAgent.Pipeline;
using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;
using KiroCliLib.Core;
using Microsoft.AspNetCore.SignalR.Client;
using ILogger = Serilog.ILogger;

namespace CodingAgent.Agent;

/// <summary>
/// Extension methods for registering chat-pod agent services (T23, arch-audit 2026-08-22).
/// Previously named <c>AgentSignalRModeRegistration</c> — renamed because both modes use SignalR;
/// the essential difference is that this mode owns no durable WorkItem row and serves interactive
/// chat sessions. Consolidation runs are work items, like every other run type.
/// Reached when the agent pod is started without <c>--work-item-id</c> (chat mode).
/// Registers <see cref="AgentWorkerService"/> and the full SignalR hub connection stack
/// (<see cref="AgentConnectionLifecycle"/>, <see cref="ChatSlotManager"/>,
/// <see cref="ChatJobExecutor"/>)
/// so the pod can serve interactive chat sessions.
/// </summary>
internal static class AgentChatModeRegistration
{
    /// <summary>
    /// Maps <c>AGENT_PROVIDER_TYPE</c> to the provider a chat pod runs. The chat dispatcher sets it
    /// to the job template's providerType ("kiro", "opencode", "claude"); the agent-provider names
    /// ("KiroCli", "OpenCode", "ClaudeCode") are accepted too. Anything else means Kiro CLI.
    /// </summary>
    internal static AgentProviderType ResolveChatProviderType(string? agentProviderType)
    {
        if (string.Equals(agentProviderType, AgentDefaults.OpenCodeHttpClientName, StringComparison.OrdinalIgnoreCase))
            return AgentProviderType.OpenCode;

        if (string.Equals(agentProviderType, AgentDefaults.ClaudeTemplateProviderType, StringComparison.OrdinalIgnoreCase)
            || string.Equals(agentProviderType, ProviderTypes.ClaudeCode, StringComparison.OrdinalIgnoreCase))
            return AgentProviderType.ClaudeCode;

        return AgentProviderType.KiroCli;
    }

    internal static IServiceCollection AddSignalRModeServices(
        this IServiceCollection services,
        ILogger logger)
    {
        services.AddSingleton<ChatSlotManager>();
        services.AddSingleton<AgentConnectionLifecycle>(sp => new AgentConnectionLifecycle(
            sp.GetRequiredService<IHubConnectionManager>(),
            sp.GetRequiredService<IHubConnectionManagerFactory>(),
            sp.GetRequiredService<AgentId>(),
            sp.GetRequiredService<IHostApplicationLifetime>(),
            logger,
            sp.GetRequiredService<AgentRuntimeOptions>()));
        services.AddSingleton<ChatJobExecutor>(sp =>
        {
            var agentId = sp.GetRequiredService<AgentId>().Value;
            var runtimeOpts = sp.GetRequiredService<AgentRuntimeOptions>();
            var providerType = ResolveChatProviderType(runtimeOpts.AgentProviderType);
            var isOpenCodeProvider = providerType == AgentProviderType.OpenCode;
            var isChatMode = runtimeOpts.IsChatMode;
            return new ChatJobExecutor(new ChatJobExecutorDependencies(
                sp.GetRequiredService<AgentConnectionLifecycle>(),
                sp.GetRequiredService<ChatSlotManager>(),
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
                        // ChatJobExecutor's own _connectionLifecycle field. Prefer capturing the singleton
                        // instances from the outer factory scope rather than re-resolving on each call.
                        var lifecycle = sp.GetRequiredService<AgentConnectionLifecycle>();
                        var lifetime = sp.GetRequiredService<IHostApplicationLifetime>();
                        await lifecycle.WaitForRegistrationAsync(lifetime.ApplicationStopping);
                        await lifecycle.Connection.InvokeAsync(HubMethodNames.AgentReady, agentId, lifetime.ApplicationStopping);
                    }
                    catch (Exception ex)
                    {
                        logger.Warning(ex, "Failed to send AgentReady signal from ChatJobExecutor");
                    }
                },
                IsOpenCodeProvider: isOpenCodeProvider,
                IsChatMode: isChatMode,
                Logger: logger)
            {
                ProviderType = providerType,
                ChatModel = runtimeOpts.ChatModel,
                ChatEffort = runtimeOpts.ChatEffort,
                ClaudeCliPath = runtimeOpts.ClaudeCliPath
            });
        });
        services.AddSingleton(sp => new AgentWorkerService(new AgentWorkerServiceDependencies(
            sp.GetRequiredService<AgentConnectionLifecycle>(),
            sp.GetRequiredService<ChatSlotManager>(),
            sp.GetRequiredService<ChatJobExecutor>(),
            logger)));
        services.AddHostedService(sp => sp.GetRequiredService<AgentWorkerService>());
        services.AddSingleton<IAgentService>(sp => sp.GetRequiredService<AgentWorkerService>());

        return services;
    }
}
