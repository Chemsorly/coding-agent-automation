using CodingAgent.Infrastructure.Persistence;
using CodingAgent.Infrastructure.Persistence.Services;
using CodingAgent.Kubernetes;
using CodingAgent.Orchestration;
using CodingAgent.Orchestration.Dispatch;
using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Services;
using Microsoft.Extensions.Configuration;
using Serilog;

namespace CodingAgent.Api;

/// <summary>
/// DI extension methods for the dispatch subsystem.
/// </summary>
public static class ApiDispatchServiceExtensions
{
    private sealed class ApiDispatchRegisteredMarker { }

    /// <summary>
    /// Registers the dispatch subsystem: resolvers, infrastructure, enricher, the queue,
    /// manual dispatch, and the services POST /api/work-items/{id}/dispatch uses to start
    /// a Pending item. Also registers <see cref="DispatchServiceOptions"/>.
    /// Structurally calls <see cref="ApiKubernetesServiceExtensions.AddApiKubernetes"/> to
    /// enforce the ordering dependency: <see cref="IKubernetesJobClient"/> must be present
    /// before <see cref="CodingAgent.Api.Dispatch.DispatchLifecycleService"/> is registered.
    /// Idempotent — safe to call multiple times; subsequent calls are no-ops.
    /// </summary>
    public static IServiceCollection AddApiDispatch(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        // Structural enforcement: ensure IKubernetesJobClient and JobTemplateStore are registered
        // before the DispatchLifecycleService factory below attempts to resolve them.
        services.AddApiKubernetes();

        if (services.Any(sd => sd.ServiceType == typeof(ApiDispatchRegisteredMarker)))
            return services;
        services.AddSingleton(new ApiDispatchRegisteredMarker());

        // ── Shared DispatchServiceOptions singleton ────────────────────────────
        // All API consumers resolve this one instance, which is already clamped by
        // DispatchServiceOptionsFactory.Create. Registered here (after AddApiKubernetes
        // which satisfies IConfiguration availability) and before AddApiChatDispatch
        // so their lambdas can resolve it via GetRequiredService<DispatchServiceOptions>().
        // The marker guard above ensures this is registered exactly once even when
        // AddApiChatDispatch calls AddApiDispatch structurally.
        //
        // TODO [WARNING]: This registration originally lived inline in AddApiOrchestration (the
        // coordinator), between the AddApiKubernetes and AddApiDispatch calls. Moving it here
        // is a minor deviation from AC3 ("each extracted class contains only the registrations
        // previously in that sub-method"). It was moved to maintain the structural guarantee
        // for AddApiChatDispatch: moving it back to the coordinator would mean a caller invoking
        // AddApiChatDispatch() directly (without the coordinator) would not have DispatchServiceOptions
        // registered — ValidateOnBuild=true catches this at startup, but it would be a silent failure
        // in production builds. If AC3 strict compliance is required, move this registration back
        // to AddApiOrchestration and rely on ValidateOnBuild for the ordering enforcement.
        services.AddSingleton(sp =>
            DispatchServiceOptionsFactory.Create(sp.GetRequiredService<IConfiguration>(), Log.Logger));

        // ── DispatchInfrastructure + AssignmentEnricher (issue #2171) ────────
        // DispatchInfrastructure aggregates ITokenVendingService, IProviderFactory,
        // ILabelService, and DispatchResolutionService. Used by AssignmentEnricher to
        // fetch fresh provider configs, steering, QGs, and issue context at assignment time.
        // Cache disabled on IConfigurationStore (registered above) so every GetAssignment
        // call sees the latest steering and QG config from the DB.
        // The API host does not have a IPipelineApiWorkItemClient in this host,
        // so includeWorkItemClient is false (workItemClient = null).
        services.AddDispatchResolutionServices(includeWorkItemClient: false);

        // ── Queue (spec 051 2.3a) ──────────────────────────────────────────────
        // WorkItemStore backs the /api/work-items enqueue, status and dedup handlers. ApiWorkDistributor
        // is IWorkDistributor in this process, over the same store, for the dispatch logic that moves
        // into the API; nothing here resolved an IWorkDistributor before.
        // Spec 053: the routing table, and the router that pins a route on every work item when it is created.
        services.AddSingleton<AgentRoutingStore>();
        services.AddSingleton<CodingAgent.Api.Dispatch.WorkItemRouter>();
        services.AddSingleton<CodingAgent.Api.Dispatch.WorkItemStore>();
        services.AddSingleton<IWorkDistributor, CodingAgent.Api.Dispatch.ApiWorkDistributor>();

        // ── Browse (spec 051 2.3b) ─────────────────────────────────────────────
        // The pages' provider browsing runs here with the stored credentials.
        services.AddSingleton<CodingAgent.Api.Browse.BacklogService>();
        services.AddSingleton<CodingAgent.Api.Browse.TemplateBrowseService>();

        // ── Manual dispatch (spec 051 2.3c) ────────────────────────────────────
        // The drawers' dispatch, moved from the web host: the orchestration enqueues through
        // ApiWorkDistributor, so a dispatched item waits in the Pending queue for the Scheduler.
        services.AddSingleton<CodingAgent.Api.Dispatch.ManualDispatchService>();

        // ── Consolidation page (spec 051 2.4b) ───────────────────────────────
        // Reads the cards and triggers through IConsolidationService (AddLifecycleAndConsolidation).
        // ConsolidationStalenessService powers the auto-start counters shown on the page (issue #3592).
        // (IDependencyChecker, IDispatchOrchestrationService, and ConsolidationStalenessService are
        // registered by AddSharedOrchestrationServices in AddOrchestrationCore.)
        services.AddSingleton<CodingAgent.Api.Consolidation.ConsolidationPageService>();

        // ── ConsolidationTemplateResolver ──────────────────────────────────────
        // Registered as a standalone singleton so AssignmentEnricher.InjectProjectSecretsAsync
        // can delegate template-ownership resolution to it (issue #2914), eliminating the
        // reimplemented loop that previously mirrored its behaviour inline.
        // Takes only IProjectStore — already registered above as a singleton.
        services.AddSingleton(sp => new ConsolidationTemplateResolver(
            sp.GetRequiredService<IProjectStore>()));
        services.AddSingleton(sp => new AssignmentEnricher(
            sp.GetRequiredService<DispatchInfrastructure>(),
            sp.GetRequiredService<IAgentProfileStore>(),
            sp.GetRequiredService<IConsolidationJobPreparationService>(),
            sp.GetRequiredService<IProjectStore>(),
            sp.GetRequiredService<ConsolidationTemplateResolver>(),
            sp.GetRequiredService<AgentRoutingStore>(),
            sp.GetRequiredService<JobTemplateStore>(),
            Log.Logger,
            new ConsolidationRunContextSources(
                sp.GetRequiredService<IPipelineRunHistoryService>()),
            sp.GetRequiredService<ITriageStore>()));

        // ── Pending dispatch services (POST /api/work-items/{id}/dispatch) ───────────────────
        // DispatchLifecycleService — shared PVC-selection lock + K8s Job creation lifecycle.
        // Used by WorkItemDispatchLoop (Scheduler) via POST /api/work-items/{id}/dispatch.
        services.AddSingleton<CodingAgent.Api.Dispatch.DispatchLifecycleService>(sp =>
        {
            var jobClient = sp.GetService<IKubernetesJobClient>();
            var options = sp.GetRequiredService<DispatchServiceOptions>();
            if (jobClient is null)
                throw new InvalidOperationException(
                    "API: IKubernetesJobClient is not registered. " +
                    "POST /api/work-items/{id}/dispatch requires a Kubernetes client. " +
                    "Register IKubernetesJobClient in DI.");
            return new CodingAgent.Api.Dispatch.DispatchLifecycleService(
                jobClient,
                sp.GetRequiredService<WorkItemTransitionService>(),
                options);
        });
        // Spec 053 DD10: routes a Pending item a previous-release API stored without a selector.
        services.AddSingleton<CodingAgent.Api.Dispatch.RolloutRouting>();

        // ── DispatchWorkItemService (issue #2743) ─────────────────────────────────────────────
        // The dispatch of a Pending item (DispatchPendingWorkItem): concurrency-snapshot query,
        // gate block and lifecycle. Singleton — stateless, depends only on JobTemplateStore.
        services.AddSingleton<CodingAgent.Api.Dispatch.DispatchWorkItemService>(sp =>
            new CodingAgent.Api.Dispatch.DispatchWorkItemService(
                sp.GetRequiredService<JobTemplateStore>()));

        return services;
    }
}
