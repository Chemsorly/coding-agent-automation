using CodingAgentWebUI.Infrastructure.Persistence;
using CodingAgentWebUI.Infrastructure.Persistence.Services;
using CodingAgentWebUI.Orchestration;
using CodingAgentWebUI.Orchestration.Dispatch;
using CodingAgentWebUI.Orchestration.LeaderElection;
using CodingAgentWebUI.Orchestration.Registry;
using CodingAgentWebUI.Pipeline.Interfaces;
using k8s;
using Microsoft.EntityFrameworkCore;
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

        // DispatchService — handles regular (non-consolidation) work items
        services.AddHostedService(sp => new DispatchService(
            sp.GetRequiredService<IDbContextFactory<PipelineDbContext>>(),
            sp.GetRequiredService<ILeaderElectionService>(),
            sp.GetRequiredService<DispatchLifecycleService>(),
            sp.GetRequiredService<IConfiguration>(),
            sp.GetRequiredService<JobTemplateStore>(),
            sp.GetService<ILabelService>(),
            sp.GetService<IAgentProfileStore>(),
            sp.GetService<IOrchestratorRunService>()));

        // ConsolidationDispatchHandler — handles consolidation work items
        services.AddHostedService(sp => new ConsolidationDispatchHandler(
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
            sp.GetService<IAgentProfileStore>()));

        services.AddHostedService(sp => new ReconciliationService(
            sp.GetRequiredService<IDbContextFactory<PipelineDbContext>>(),
            sp.GetRequiredService<ILeaderElectionService>(),
            sp.GetRequiredService<IKubernetes>(),
            sp.GetRequiredService<WorkItemTransitionService>(),
            sp.GetRequiredService<IConfiguration>(),
            sp.GetService<ILabelService>(),
            sp.GetService<IRunLifecycleManager>(),
            sp.GetService<IConsolidationService>(),
            sp.GetService<IConfigurationStore>(),
            sp.GetService<IJobDeduplicationGuard>()));

        // HeartbeatMonitorService NOT registered in K8s mode (agent liveness via ReconciliationService)
        // JobQueueDrainService NOT registered (work distribution via IWorkDistributor)

        // Queue visibility: queries WorkItems table for Pending status
        services.AddSingleton<IPendingWorkQuery>(sp =>
            new DbPendingWorkQuery(sp.GetRequiredService<IDbContextFactory<PipelineDbContext>>()));

        Log.Information("WorkDistribution: Kubernetes mode — DispatchService + ConsolidationDispatchHandler + ReconciliationService + LeaderElection registered");
    }
}
