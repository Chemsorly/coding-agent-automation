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
    /// - Kubernetes client (explicit in-cluster-first branch per 041 Req 5.9)
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
        // ── Kubernetes client (explicit in-cluster-first; 041 Req 5.9) ────────
        services.AddKubernetesClient();
        services.AddSingleton<IKubernetesJobClient, KubernetesJobClient>();

        // ── Job template store ────────────────────────────────────────────────
        var templatesPath = configuration.GetValue<string>("WorkDistribution:JobTemplatesPath")
            ?? "/app/config/job-templates.yaml";
        services.AddSingleton(_ =>
        {
            // Graceful fallback when path doesn't exist (e.g., integration test environments)
            if (!File.Exists(templatesPath))
            {
                Serilog.Log.Warning("Job templates file not found at {Path}; starting with empty template store", templatesPath);
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
            });

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
