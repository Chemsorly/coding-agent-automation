using CodingAgentWebUI.JobController.Dispatch;
using CodingAgentWebUI.JobController.Reconciliation;
using CodingAgentWebUI.Kubernetes;
using CodingAgentWebUI.Pipeline.LeaderElection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace CodingAgentWebUI.JobController;

/// <summary>
/// Service registration extensions for the Job Controller host.
/// </summary>
public static class JobControllerServiceCollectionExtensions
{
    /// <summary>
    /// Registers all Job Controller services:
    /// - Kubernetes client (in-cluster-first with kubeconfig fallback)
    /// - KubernetesJobClient
    /// - JobTemplateStore
    /// - ILeaderElectionService / LeaderElectionService
    /// - DispatchService (with DispatchLoop and PvcPool)
    /// - ReconciliationService (with ReconciliationLoop)
    /// </summary>
    public static IServiceCollection AddJobControllerServices(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        // ── Kubernetes client (in-cluster-first with kubeconfig fallback) ─────
        services.AddKubernetesClient();
        services.AddSingleton<IKubernetesJobClient, KubernetesJobClient>();

        // ── Job template store ────────────────────────────────────────────────
        var templatesPath = configuration.GetValue<string>("WorkDistribution:JobTemplatesPath")
            ?? "/app/config/job-templates.yaml";
        services.AddSingleton(_ =>
        {
            // Fatal misconfiguration: if the templates file is missing in production, the controller
            // will start but dispatch nothing — every work item will be silently skipped. Log at
            // Error so monitoring alerts fire. Use Log.Warning only in intentional test environments
            // (e.g., integration tests) where the empty store is deliberate.
            if (!File.Exists(templatesPath))
            {
                Serilog.Log.Error(
                    "Job templates file not found at {Path}. The Job Controller will start but " +
                    "dispatch NO work items until this is resolved. Check WorkDistribution:JobTemplatesPath " +
                    "and the ConfigMap mount.", templatesPath);
                return JobTemplateStore.CreateEmpty();
            }
            return JobTemplateStore.LoadFromFile(templatesPath);
        });

        // ── Leader election ───────────────────────────────────────────────────
        // Uses DispatchLeaseName for both DispatchService and ReconciliationService
        services.AddOptions<LeaderElectionOptions>()
            .Configure<IConfiguration>((opts, cfg) =>
            {
                cfg.GetSection(LeaderElectionOptions.SectionName).Bind(opts);

                // Allow override: DispatchLeaseName takes precedence over generic LeaseName
                var dispatchLeaseName = cfg.GetValue<string>("LeaderElection:DispatchLeaseName");
                if (!string.IsNullOrEmpty(dispatchLeaseName))
                    opts.LeaseName = dispatchLeaseName;
                else if (string.IsNullOrEmpty(opts.LeaseName))
                    opts.LeaseName = "caa-dispatch-lock";
            })
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddSingleton<ILeaderElectionService, LeaderElectionService>(sp =>
            new LeaderElectionService(
                sp.GetRequiredService<IOptions<LeaderElectionOptions>>(),
                sp.GetRequiredService<k8s.IKubernetes>()));
        services.AddHostedService(sp => (LeaderElectionService)sp.GetRequiredService<ILeaderElectionService>());

        // ── Dispatch service ──────────────────────────────────────────────────
        services.AddDispatchService(configuration);

        // ── Reconciliation service ────────────────────────────────────────────
        services.AddReconciliationService();

        return services;
    }
}
