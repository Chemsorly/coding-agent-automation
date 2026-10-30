using CodingAgent.AgentGateway;
using CodingAgent.Kubernetes;
using CodingAgent.Orchestration.Dispatch;
using CodingAgent.Orchestration.Health;
using CodingAgent.Orchestration.Redis;
using CodingAgent.Orchestration.Registry;
using CodingAgent.Pipeline.Interfaces;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Hosting;
using Serilog;

namespace CodingAgent.Api;

/// <summary>
/// DI extension methods for chat dispatch services.
/// </summary>
public static class ApiChatDispatchServiceExtensions
{
    private sealed class ApiChatDispatchRegisteredMarker { }

    /// <summary>
    /// Registers chat dispatch services: model fetch job service, ChatJobDispatcher hosted
    /// service, and the IChatJobDispatcher forwarding registration.
    /// Structurally calls <see cref="ApiDispatchServiceExtensions.AddApiDispatch"/> to
    /// enforce the ordering dependency: <see cref="IKubernetesJobClient"/> and
    /// <see cref="DispatchServiceOptions"/> must be present before <see cref="ChatJobDispatcher"/>
    /// and <see cref="ModelFetchJobService"/> are registered.
    /// Idempotent — safe to call multiple times; subsequent calls are no-ops.
    /// </summary>
    public static IServiceCollection AddApiChatDispatch(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        // Structural enforcement: ensure IKubernetesJobClient, JobTemplateStore, and
        // DispatchServiceOptions are registered before ChatJobDispatcher and
        // ModelFetchJobService factories below attempt to resolve them.
        services.AddApiDispatch();

        if (services.Any(sd => sd.ServiceType == typeof(ApiChatDispatchRegisteredMarker)))
            return services;
        services.AddSingleton(new ApiChatDispatchRegisteredMarker());

        // ── WorkItemMetricsBackgroundService ──────────────────────────────────────────────────
        // Spec 047: Removed from API hosted services — replaced by WorkItemCountsService in
        // CodingAgent.Scheduler. WorkItemCountsService polls GET /api/work-items/counts-by-status
        // and registers the same WorkDistributionTelemetry callback from the Scheduler process.

        // ── ModelFetchJobService ─────────────────────────────────────────────────────────────
        // Singleton in the API. The API has K8s RBAC for batch/jobs.
        services.AddSingleton<ModelFetchJobService>(sp => new ModelFetchJobService(
            new ModelFetchJobDependencies(
                sp.GetRequiredService<IKubernetesJobClient>(),
                sp.GetRequiredService<JobTemplateStore>(),
                sp.GetRequiredService<DispatchServiceOptions>(),
                sp.GetRequiredService<IPipelineConfigStore>(),
                sp.GetRequiredService<ModelFetchService>(),
                Logger: Log.Logger)));

        // ── ChatJobDispatcher — on-demand ephemeral chat pod dispatch ────────────────────────
        // Moved from the Blazor monolith to the API host (Spec 044/045 follow-up).
        // The monolith no longer maps AgentHub, so IHubContext<AgentHub> on the monolith was
        // disconnected from any real agents. The API host owns the hub and the AgentRegistryService,
        // making it the correct process for chat dispatch and the registry poll loop.
        // Spec 049: ILeaderElectionService removed — all replicas can dispatch. The K8s
        // job-list query is used only for PVC availability.
        //
        // ChatHeartbeatTracker and ChatSessionWatcher are internal to CodingAgent.AgentGateway.
        // The public ChatJobDispatcher constructor accepts IRedisStore? and constructs the
        // collaborators internally, so CodingAgent.Api does not need to reference internal types.
        services.AddSingleton<ChatJobDispatcher>(sp =>
        {
            var options = sp.GetRequiredService<DispatchServiceOptions>();
            var redisStore = sp.GetService<IRedisStore>();
            return new ChatJobDispatcher(
                sp.GetRequiredService<IKubernetesJobClient>(),
                sp.GetRequiredService<IHubContext<AgentHub, IAgentHubClient>>(),
                sp.GetRequiredService<JobTemplateStore>(),
                sp.GetRequiredService<IAgentRegistryService>(),
                options,
                Log.Logger,
                redisStore);
        });
        services.AddHostedService(sp => sp.GetRequiredService<ChatJobDispatcher>());
        services.AddSingleton<IChatJobDispatcher>(sp => sp.GetRequiredService<ChatJobDispatcher>());

        // ── Agent Chat page (spec 051 2.5b) ──────────────────────────────────────────────────
        // Who launched each chat pod, and what its prompts are built from. A record outlives the
        // longest chat job by an hour; ending the chat deletes it. Clamped like the dispatcher's
        // options (warnings are logged once, by the dispatcher).
        services.AddSingleton(sp =>
        {
            var options = sp.GetRequiredService<DispatchServiceOptions>();
            return new CodingAgent.Api.Chat.ChatSessionStore(
                sp.GetService<IRedisStore>(), TimeSpan.FromSeconds(options.ChatJobMaxDurationSeconds) + TimeSpan.FromHours(1));
        });
        services.AddSingleton(sp =>
        {
            var options = sp.GetRequiredService<DispatchServiceOptions>();
            return ActivatorUtilities.CreateInstance<CodingAgent.Api.Chat.ChatService>(sp, options);
        });

        return services;
    }
}
