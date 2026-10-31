using CodingAgent.AgentGateway;
using CodingAgent.Infrastructure.Persistence;
using CodingAgent.Infrastructure.Persistence.Services;
using CodingAgent.Kubernetes;
using CodingAgent.Orchestration;
using CodingAgent.Orchestration.Health;
using CodingAgent.Orchestration.Redis;
using CodingAgent.Orchestration.Registry;
using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Services;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Serilog;

namespace CodingAgent.Api;

/// <summary>
/// DI extension methods for lifecycle, consolidation, agent communication, and run management services.
/// </summary>
public static class ApiLifecycleServiceExtensions
{
    private sealed class ApiLifecycleAndConsolidationRegisteredMarker { }

    /// <summary>
    /// Registers lifecycle, consolidation, agent communication, and run management services.
    /// Structurally calls <see cref="ApiTokenVendingServiceExtensions.AddApiTokenVending"/> to
    /// enforce the ordering dependency: <see cref="ITokenVendingService"/> must be present before
    /// <see cref="IConsolidationJobPreparationService"/> is registered.
    /// Idempotent — safe to call multiple times; subsequent calls are no-ops.
    /// </summary>
    public static IServiceCollection AddApiLifecycleAndConsolidation(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        // Structural enforcement: ensure ITokenVendingService (and ILabelService) are registered
        // before the IConsolidationJobPreparationService factory below attempts to resolve them.
        services.AddApiTokenVending();

        if (services.Any(sd => sd.ServiceType == typeof(ApiLifecycleAndConsolidationRegisteredMarker)))
            return services;
        services.AddSingleton(new ApiLifecycleAndConsolidationRegisteredMarker());

        // ── PipelineRunLifecycleService — implements IChangeNotifier + IChatNotifier ──
        services.AddSingleton<PipelineRunLifecycleService>(sp => new PipelineRunLifecycleService(
            sp.GetRequiredService<IPipelineRunHistoryService>(),
            sp.GetRequiredService<IOrchestratorRunService>(),
            Log.Logger,
            sp.GetService<IAgentCancellationSender>()));
        services.AddSingleton<IChangeNotifier>(sp => sp.GetRequiredService<PipelineRunLifecycleService>());
        services.AddSingleton<IChatNotifier>(sp => sp.GetRequiredService<PipelineRunLifecycleService>());

        // ── IAgentCommunication → SignalRAgentCommunication ──────────────────
        // Registered before ModelFetchService which depends on it.
        // TODO [WARNING]: The comment "Registered before ModelFetchService which depends on it"
        // implies registration order matters. It does not — MS DI resolves singleton factory
        // lambdas lazily at first-use, not at registration time. The comment is inaccurate and
        // may mislead future readers into believing eager ordering constraints apply here.
        // IAgentCommunication was moved earlier (before ConsolidationBadgeService) in this
        // refactor; the move is safe but the ordering rationale in the comment is wrong.
        // See review finding [WARNING] ApiServiceCollectionExtensions.cs:423 (DotNetSpecialist review).
        services.AddSingleton<IAgentCommunication>(sp =>
            new SignalRAgentCommunication(
                sp.GetRequiredService<IHubContext<AgentHub, IAgentHubClient>>()));

        // ── ModelFetchService ────────────────────────────────────────────────
        services.AddSingleton<ModelFetchService>(sp => new ModelFetchService(
            sp.GetRequiredService<IAgentRegistryService>(),
            sp.GetRequiredService<IAgentCommunication>(),
            Log.Logger,
            sp.GetService<IRedisStore>()));  // null when Redis is not configured

        // ── ConsolidationBadgeService ────────────────────────────────────────
        services.AddSingleton<ConsolidationBadgeService>();

        // ── LoopStatusStore — the Scheduler leader's last loop status (spec 051 D11) ──
        services.AddSingleton(sp => new CodingAgent.Api.Loop.LoopStatusStore(
            sp.GetService<IRedisStore>(), sp.GetRequiredService<TimeProvider>()));

        // ── IActiveRunQueryService ────────────────────────────────────────────
        services.AddSingleton<IActiveRunQueryService>(sp => new PostgresActiveRunQueryService(
            sp.GetRequiredService<IDbContextFactory<PipelineDbContext>>(),
            sp.GetRequiredService<IOrchestratorRunService>()));

        // ── IRunLifecycleManager ──────────────────────────────────────────────
        services.AddSingleton<IRunLifecycleManager>(sp => new RunLifecycleManager(
            new RunLifecycleManagerDependencies(
                sp.GetRequiredService<IOrchestratorRunService>(),
                sp.GetRequiredService<IPipelineRunHistoryService>(),
                sp.GetRequiredService<IAgentRegistryService>(),
                sp.GetRequiredService<ILabelService>(),
                Log.Logger,
                sp.GetService<IJobCleanupStrategy>(),
                sp.GetRequiredService<IWorkItemFallbackTransitionService>())));

        // ── WorkItemStatusTransitionService (issue #2914) ──────────────────────
        // Encapsulates the compound status-transition orchestration extracted from
        // WorkItemAgentEndpoints.PostStatus: infra-recovery guard, pre-read idempotency guard,
        // TransitionDetailedAsync, lifecycle dispatch, and telemetry fire-and-forget.
        // Placed in CodingAgent.Api (not CodingAgent.Orchestration) to respect the
        // Orchestration_ShouldNot_ReferenceInfrastructurePersistenceAssembly arch boundary.
        // Depends on IRunLifecycleManager — registered in this same sub-method above.
        services.AddSingleton(sp => new WorkItemStatusTransitionService(
            sp.GetRequiredService<WorkItemTransitionService>(),
            sp.GetRequiredService<IRunLifecycleManager>(),
            // TODO: [WARNING] Resolves logger via ILoggerFactory rather than ILogger<T> directly.
            // Both produce a correctly typed logger at runtime, but the ILoggerFactory approach
            // bypasses any ILogger<T> registrations or decorators held by the DI container (e.g.
            // scoped test loggers, xUnit sink registrations). The idiomatic pattern is:
            //   sp.GetRequiredService<ILogger<WorkItemStatusTransitionService>>()
            sp.GetRequiredService<ILoggerFactory>().CreateLogger<WorkItemStatusTransitionService>(),
            sp.GetRequiredService<IDbContextFactory<PipelineDbContext>>()));

        // ── IConsolidationJobPreparationService ────────────────────────────
        // Required by AssignmentEnricher to resolve provider configs and vend short-lived tokens
        // at assignment time (GET /api/work-items/{id}/assignment).
        // Also used by ReportConsolidationComplete hub handling.
        // ITokenVendingService is guaranteed to be registered by the structural call to
        // AddApiTokenVending() at the top of this method.
        services.AddSingleton<IConsolidationJobPreparationService>(sp =>
            new ConsolidationJobPreparationService(
                sp.GetRequiredService<IProviderConfigStore>(),
                sp.GetRequiredService<IProjectStore>(),
                sp.GetRequiredService<ITokenVendingService>(),
                Log.Logger,
                sp.GetRequiredService<IPipelineConfigStore>()));

        return services;
    }
}
