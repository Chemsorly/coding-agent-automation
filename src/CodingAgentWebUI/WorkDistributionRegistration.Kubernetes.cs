using CodingAgentWebUI.Hubs;
using CodingAgentWebUI.Infrastructure.Persistence;
using CodingAgentWebUI.Infrastructure.Persistence.Services;
using CodingAgentWebUI.Orchestration;
using CodingAgentWebUI.Orchestration.Dispatch;
using CodingAgentWebUI.Orchestration.Health;
using CodingAgentWebUI.Orchestration.LeaderElection;
using CodingAgentWebUI.Orchestration.Registry;
using CodingAgentWebUI.Pipeline.Interfaces;
using k8s;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Serilog;

namespace CodingAgentWebUI;

public static partial class WorkDistributionRegistration
{
    private static void RegisterKubernetesMode(IServiceCollection services, IConfiguration configuration)
    {
        // K8s client
        services.AddSingleton<IKubernetes>(_ =>
        {
            var config = KubernetesClientConfiguration.InClusterConfig();
            return new Kubernetes(config);
        });

        // Leader election
        services.Configure<LeaderElectionOptions>(configuration.GetSection(LeaderElectionOptions.SectionName));
        services.AddSingleton<LeaderElectionService>();
        services.AddSingleton<ILeaderElectionService>(sp => sp.GetRequiredService<LeaderElectionService>());
        services.AddHostedService(sp => sp.GetRequiredService<LeaderElectionService>());

        // Work distributor (singleton — uses IDbContextFactory for context-per-operation)
        services.AddSingleton<IWorkDistributor>(sp => new KubernetesWorkDistributor(
            sp.GetRequiredService<IDbContextFactory<PipelineDbContext>>(),
            sp.GetRequiredService<WorkItemTransitionService>(),
            sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<KubernetesWorkDistributor>>()));

        // Dispatch + Reconciliation (under leader election)
        services.AddSingleton<IKubernetesJobClient>(sp => new KubernetesJobClient(sp.GetRequiredService<IKubernetes>()));
        services.AddSingleton<IJobCleanupStrategy>(sp => new KubernetesJobCleanup(
            sp.GetRequiredService<IDbContextFactory<PipelineDbContext>>(),
            sp.GetRequiredService<IKubernetesJobClient>(),
            configuration.GetValue<string>("WorkDistribution:Namespace")
                ?? Environment.GetEnvironmentVariable("POD_NAMESPACE")
                ?? "default",
            Log.Logger));

        // Shared dispatch lifecycle service
        services.AddSingleton<DispatchLifecycleService>(sp =>
        {
            var options = DispatchServiceOptionsFactory.Create(sp.GetRequiredService<IConfiguration>());
            return new DispatchLifecycleService(
                sp.GetRequiredService<IKubernetesJobClient>(),
                sp.GetRequiredService<WorkItemTransitionService>(),
                options);
        });

        // JobTemplateStore — single load, shared by DispatchService, ConsolidationDispatchHandler, and UI validation.
        services.AddSingleton<JobTemplateStore>(sp =>
            DispatchService.LoadTemplateProvider(sp.GetRequiredService<IConfiguration>()));

        // DispatchStateBuilder — shared state-building logic for DispatchService and ConsolidationDispatchHandler.
        // TODO: DispatchServiceOptionsFactory.Create() is called once here and separately when DispatchLifecycleService
        // is registered, producing two distinct DispatchServiceOptions instances. Configuration is static at startup
        // (K8s rolling restarts pick up ConfigMap changes), so the instances will be identical in practice. If options
        // ever become mutable or need to stay in sync, pass the already-registered DispatchLifecycleService's options
        // instance instead of calling Create() again.
        services.AddSingleton<DispatchStateBuilder>(sp => new DispatchStateBuilder(
            sp.GetRequiredService<IDbContextFactory<PipelineDbContext>>(),
            sp.GetRequiredService<DispatchLifecycleService>(),
            sp.GetRequiredService<JobTemplateStore>(),
            new DispatchTemplateResolver(
                // TODO: sp.GetService<IAgentProfileStore>() returns null if IAgentProfileStore is not
                // registered (e.g. in a non-Kubernetes deployment variant). DispatchTemplateResolver
                // accepts a nullable store and silently suppresses agent-profile lookups when it is null,
                // rather than failing at startup. This is consistent with the existing pattern in
                // DispatchService and ConsolidationDispatchHandler, but the explicit singleton registration
                // here is the natural place to tighten it. See DotNetSpecialist WARNING (Issue #1910).
                sp.GetService<IAgentProfileStore>(),
                sp.GetRequiredService<JobTemplateStore>()),
            DispatchServiceOptionsFactory.Create(sp.GetRequiredService<IConfiguration>())));

        // DispatchService — handles regular (non-consolidation) work items
        // ILabelSwapService is built inline (not registered as a named singleton) because it is optional:
        // when ILabelService is not configured, LabelSwapper is null and DispatchService skips label swap.
        // maxAttempts=1: single attempt + reconciliation flag on failure (no retry in K8s mode). (#1868)
        services.AddHostedService(sp => new DispatchService(
            new DispatchServiceCoreDependencies(
                sp.GetRequiredService<IDbContextFactory<PipelineDbContext>>(),
                sp.GetRequiredService<ILeaderElectionService>(),
                sp.GetRequiredService<DispatchLifecycleService>(),
                LabelSwapper: sp.GetService<ILabelService>() is { } ls
                    ? new LabelSwapService(
                        ls,
                        sp.GetRequiredService<IDbContextFactory<PipelineDbContext>>(),
                        sp.GetRequiredService<ILogger<LabelSwapService>>(),
                        maxAttempts: 1)
                    : null,
                sp.GetService<IAgentProfileStore>(),
                sp.GetService<IOrchestratorRunService>(),
                sp.GetRequiredService<DispatchStateBuilder>()),
            sp.GetRequiredService<IConfiguration>(),
            sp.GetRequiredService<JobTemplateStore>()));

        // ConsolidationDispatchHandler — handles consolidation work items
        services.AddHostedService(sp => new ConsolidationDispatchHandler(
            new ConsolidationDispatchHandlerDependencies(
                sp.GetRequiredService<IDbContextFactory<PipelineDbContext>>(),
                sp.GetRequiredService<ILeaderElectionService>(),
                sp.GetRequiredService<DispatchLifecycleService>(),
                sp.GetRequiredService<JobTemplateStore>(),
                sp.GetRequiredService<IConfiguration>(),
                sp.GetRequiredService<WorkItemTransitionService>(),
                sp.GetService<IConsolidationRunStore>(),
                sp.GetService<IConsolidationService>(),
                sp.GetService<IConsolidationJobPreparationService>(),
                sp.GetService<IPipelineConfigStore>(),
                sp.GetService<IProjectStore>(),
                sp.GetService<IAgentProfileStore>(),
                sp.GetRequiredService<DispatchStateBuilder>())));

        services.AddHostedService(sp => new ReconciliationService(
            new ReconciliationServiceDependencies(
                sp.GetRequiredService<IDbContextFactory<PipelineDbContext>>(),
                sp.GetRequiredService<ILeaderElectionService>(),
                sp.GetRequiredService<IKubernetes>(),
                sp.GetRequiredService<WorkItemTransitionService>(),
                sp.GetRequiredService<IConfiguration>(),
                sp.GetService<ILabelService>(),
                sp.GetService<IRunLifecycleManager>(),
                sp.GetService<IConsolidationService>(),
                sp.GetService<IConfigurationStore>(),
                sp.GetService<IJobDeduplicationGuard>())));

        // HeartbeatMonitorService NOT registered in K8s mode (agent liveness via ReconciliationService)
        // JobQueueDrainService NOT registered (work distribution via IWorkDistributor)

        // Queue visibility: queries WorkItems table for Pending status
        services.AddSingleton<IPendingWorkQuery>(sp =>
            new DbPendingWorkQuery(sp.GetRequiredService<IDbContextFactory<PipelineDbContext>>()));

        // ChatJobDispatcher — on-demand ephemeral chat pod dispatch (K8s mode)
        // ChatJobDispatcher is in the web project (requires IHubContext<AgentHub>).
        services.AddSingleton<ChatJobDispatcher>(sp =>
        {
            var options = DispatchServiceOptionsFactory.Create(sp.GetRequiredService<IConfiguration>());
            options.ValidateAndClamp(Log.Logger);
            return new ChatJobDispatcher(
                sp.GetRequiredService<IKubernetesJobClient>(),
                sp.GetRequiredService<IHubContext<AgentHub, IAgentHubClient>>(),
                sp.GetRequiredService<JobTemplateStore>(),
                sp.GetRequiredService<AgentRegistryService>(),
                options,
                sp.GetRequiredService<ILeaderElectionService>(),
                Log.Logger);
        });
        services.AddHostedService(sp => sp.GetRequiredService<ChatJobDispatcher>());
        services.AddSingleton<IChatJobDispatcher>(sp => sp.GetRequiredService<ChatJobDispatcher>());

        // ModelFetchJobService — k8s-mode Fetch Models via one-shot agent job + SignalR response
        services.AddSingleton<ModelFetchJobService>(sp => new ModelFetchJobService(
            new ModelFetchJobDependencies(
                sp.GetRequiredService<IKubernetesJobClient>(),
                sp.GetRequiredService<JobTemplateStore>(),
                DispatchServiceOptionsFactory.Create(sp.GetRequiredService<IConfiguration>()),
                sp.GetRequiredService<IPipelineConfigStore>(),
                sp.GetRequiredService<ModelFetchService>(),
                Logger: Log.Logger)));

        Log.Information("WorkDistribution: Kubernetes mode — DispatchService + ConsolidationDispatchHandler + ReconciliationService + LeaderElection registered");
    }
}
