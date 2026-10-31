using CodingAgent.Infrastructure.Persistence;
using CodingAgent.Kubernetes;
using CodingAgent.Orchestration.Dispatch;
using CodingAgent.Pipeline.Interfaces;
using k8s;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Serilog;
using Serilog.Events;

namespace CodingAgent.Api;

/// <summary>
/// DI extension methods for Kubernetes services.
/// </summary>
public static class ApiKubernetesServiceExtensions
{
    private sealed class ApiKubernetesRegisteredMarker { }

    /// <summary>
    /// Registers Kubernetes client, job client, cleanup strategy, job template store,
    /// and database maintenance service.
    /// Idempotent — safe to call multiple times; subsequent calls are no-ops.
    /// </summary>
    public static IServiceCollection AddApiKubernetes(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        if (services.Any(sd => sd.ServiceType == typeof(ApiKubernetesRegisteredMarker)))
            return services;
        services.AddSingleton(new ApiKubernetesRegisteredMarker());

        // ── IKubernetes ──────────────────────────────────────────────────────────────────────
        // Required by IKubernetesJobClient (ModelFetchJobService, ChatJobDispatcher).
        services.AddSingleton<IKubernetes>(_ =>
            KubernetesClientRegistration.TryCreate(
                callerLabel: "API",
                callerNote: "DatabaseMaintenanceService sweep runs ungated.")!);

        // IKubernetesJobClient wraps IKubernetes. Use GetService (nullable) so the factory
        // returns a no-op stub when K8s is unavailable, instead of passing null to
        // KubernetesJobClient which would NRE on the first dispatch attempt.
        services.AddSingleton<IKubernetesJobClient>(sp =>
        {
            var k8s = sp.GetService<IKubernetes>();
            if (k8s is null)
            {
                Log.Warning("API: IKubernetesJobClient unavailable — K8s not configured. ModelFetch and ChatJobDispatcher will fail if triggered.");
                return null!;
            }
            return new KubernetesJobClient(k8s);
        });

        // ── IJobCleanupStrategy (1D-003) ─────────────────────────────────────
        // Registered here so RunLifecycleManager.CancelRunAsync (and FailRunAsync) can delete
        // the K8s Job on cancellation/failure, preventing the pod from consuming backoffLimit retries.
        // Gracefully degrades to no-op when IKubernetesJobClient is null (K8s unavailable).
        services.AddSingleton<IJobCleanupStrategy>(sp =>
        {
            var jobClient = sp.GetService<IKubernetesJobClient>();
            if (jobClient is null)
            {
                Log.Warning("API: IJobCleanupStrategy unavailable — IKubernetesJobClient not registered. K8s Jobs will not be deleted on cancel/fail.");
                return new NoOpJobCleanupStrategy();
            }
            var cfg = sp.GetRequiredService<IConfiguration>();
            var ns = cfg.GetValue<string>("WorkDistribution:Namespace") ?? "coding-agent";
            return new KubernetesJobCleanup(
                new DbWorkItemClientAdapter(sp.GetRequiredService<IDbContextFactory<PipelineDbContext>>()),
                jobClient,
                ns,
                Log.Logger);
        });

        // ── JobTemplateStore ─────────────────────────────────────────────────
        // Required by ModelFetchJobService and ChatJobDispatcher.
        // Path matches WorkDistribution__JobTemplatesPath env var set in api-deployment.yaml.
        services.AddSingleton(sp =>
            JobTemplateStore.LoadFromConfiguration(
                sp.GetRequiredService<IConfiguration>(),
                LogEventLevel.Warning,
                "Job templates file not found at {Path}; starting with empty template store"));

        // ── DatabaseMaintenanceService ────────────────────────────────────────────────────────
        // The only retention sweep in the system — orphaning it causes Postgres to grow
        // without bound while retention settings still render in the UI.
        // Registered as a singleton (not hosted) so the maintenance endpoint in ApiSchedulerEndpoints
        // can resolve and invoke RunRetentionSweepAsync directly. The Scheduler triggers sweeps
        // via POST /api/scheduler/maintenance/retention-sweep.
        // Leader gating removed (Spec 049): the Scheduler's RetentionSweepSchedulerService
        // gates on its own leader election — no API-side lease needed.
        services.AddSingleton<DatabaseMaintenanceService>(sp => new DatabaseMaintenanceService(
            sp.GetRequiredService<IDbContextFactory<PipelineDbContext>>(),
            sp.GetRequiredService<IConfiguration>(),
            sp.GetRequiredService<IPipelineConfigStore>(),
            sp.GetRequiredService<ITriageStore>()));

        return services;
    }
}
