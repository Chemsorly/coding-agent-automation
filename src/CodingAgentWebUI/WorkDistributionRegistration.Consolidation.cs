using CodingAgentWebUI.Api.Client;
using CodingAgentWebUI.Hub;
using CodingAgentWebUI.Infrastructure.Persistence;
using CodingAgentWebUI.Infrastructure.Persistence.Services;
using CodingAgentWebUI.Kubernetes;
using CodingAgentWebUI.Orchestration.Dispatch;
using CodingAgentWebUI.Orchestration.Registry;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.LeaderElection;
using k8s;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Serilog;

namespace CodingAgentWebUI;

public static partial class WorkDistributionRegistration
{
    /// <summary>
    /// Registers services for Kubernetes work distribution:
    /// - K8s infrastructure (IKubernetes, IKubernetesJobClient, IJobCleanupStrategy)
    /// - Leader election (ILeaderElectionService)
    /// - IWorkDistributor (KubernetesWorkDistributor — fully API-backed, no EF)
    /// - JobTemplateStore
    /// - Pipeline API client
    ///
    /// Both KubernetesWorkDistributor and KubernetesJobCleanup are API-backed.
    /// Remaining IDbContextFactory consumers are in WorkDistributionRegistration.cs.
    /// </summary>
    private static void RegisterConsolidationServices(IServiceCollection services, IConfiguration configuration)
    {
        // ── Pipeline API client safety fallback for test environments ──────────────────────────
        // AddPipelineApiClient is registered unconditionally in Program.cs before AddWorkDistribution.
        // This block is a fallback for tests that call AddWorkDistribution directly without Program.cs.
        if (!services.Any(sd => sd.ServiceType == typeof(PipelineApiClientOptions)))
        {
            var pipelineApiBaseUrl = configuration.GetValue<string>("PipelineApi:BaseUrl") ?? "";
            var agentApiKey = configuration.GetValue<string>("AGENT_API_KEY")
                ?? Environment.GetEnvironmentVariable("AGENT_API_KEY")
                ?? "";
            if (!string.IsNullOrEmpty(pipelineApiBaseUrl) && !string.IsNullOrEmpty(agentApiKey))
            {
                services.AddPipelineApiClient(new PipelineApiClientOptions
                {
                    BaseUrl = pipelineApiBaseUrl,
                    AgentApiKey = agentApiKey
                });
                Log.Information("WorkDistribution: Pipeline API client registered (BaseUrl={BaseUrl})", pipelineApiBaseUrl);
            }
            else
            {
                Log.Warning("WorkDistribution: PipelineApi:BaseUrl or AGENT_API_KEY not configured — " +
                            "IPipelineApiWorkItemClient will not be registered. " +
                            "KubernetesWorkDistributor will fail at startup if PipelineApi is required.");
            }
        }

        // ── K8s client — in-cluster-first with kubeconfig fallback for local dev ──
        services.AddSingleton<IKubernetes>(_ =>
        {
            var inCluster = KubernetesClientConfiguration.IsInCluster();
            var config = inCluster
                ? KubernetesClientConfiguration.InClusterConfig()
                : KubernetesClientConfiguration.BuildDefaultConfig();

            Log.Information("Kubernetes client configured: Source={Source} Host={Host}",
                inCluster ? "in-cluster" : "kubeconfig", config.Host);

            if (string.IsNullOrEmpty(config.Host) || config.Host == "http://localhost:8080")
            {
                Log.Fatal("No usable Kubernetes configuration. In-cluster: ensure the service account " +
                          "token is mounted. Outside a cluster: set KUBECONFIG or provide ~/.kube/config.");
                throw new InvalidOperationException("No usable Kubernetes configuration.");
            }

            return new k8s.Kubernetes(config);
        });

        // ── Leader election ───────────────────────────────────────────────────
        services.Configure<LeaderElectionOptions>(configuration.GetSection(LeaderElectionOptions.SectionName));
        services.AddSingleton<LeaderElectionService>();
        services.AddSingleton<ILeaderElectionService>(sp => sp.GetRequiredService<LeaderElectionService>());
        services.AddHostedService(sp => sp.GetRequiredService<LeaderElectionService>());

        // ── IKubernetesJobClient ─────────────────────────────────────────────
        services.AddSingleton<IKubernetesJobClient>(sp => new KubernetesJobClient(sp.GetRequiredService<IKubernetes>()));

        // ── IJobCleanupStrategy ──────────────────────────────────────────────
        // KubernetesJobCleanup now uses IPipelineApiWorkItemClient.GetK8sJobNameAsync
        // instead of IDbContextFactory — DB dependency removed.
        services.AddSingleton<IJobCleanupStrategy>(sp => new KubernetesJobCleanup(
            sp.GetRequiredService<IPipelineApiWorkItemClient>(),
            sp.GetRequiredService<IKubernetesJobClient>(),
            configuration.GetValue<string>("WorkDistribution:Namespace")
                ?? Environment.GetEnvironmentVariable("POD_NAMESPACE")
                ?? "default",
            Log.Logger));

        // ── IWorkDistributor (KubernetesWorkDistributor) ─────────────────────
        // KubernetesWorkDistributor is now fully API-backed — no IDbContextFactory,
        // no WorkItemTransitionService. Distribute, cancel, status, and dedup all
        // route through IPipelineApiWorkItemClient.
        services.AddSingleton<IWorkDistributor>(sp =>
        {
            var apiClient = sp.GetService<IPipelineApiWorkItemClient>();
            if (apiClient is null)
            {
                Log.Warning("WorkDistribution: IPipelineApiWorkItemClient not registered — KubernetesWorkDistributor cannot function. " +
                            "This is expected in test environments without PipelineApi configured.");
            }
            return new KubernetesWorkDistributor(
                apiClient!,
                sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<KubernetesWorkDistributor>>());
        });

        // ── JobTemplateStore ─────────────────────────────────────────────────
        services.AddSingleton<JobTemplateStore>(sp =>
            DispatchService.LoadTemplateProvider(sp.GetRequiredService<IConfiguration>()));

        // ── IPendingWorkQuery — not registered
        // dispatch.queue.depth gauge backed by DbPendingWorkQuery was removed.
        // TODO(Spec 046): restore via GET /api/work-items/pending-count on IPipelineApiWorkItemClient.

        // ── ChatJobDispatcher — on-demand ephemeral chat pod dispatch ────────────
        // Registered in the monolith because it still injects IHubContext<AgentHub, IAgentHubClient>.
        // The disconnected IHubContext path (AssignChatPrompt cannot reach agents on the API hub)
        // is tracked for TODO(Spec 046) — a REST endpoint on the API will replace this.
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

        Log.Information("WorkDistribution: Kubernetes infrastructure registered (LeaderElection, K8s client)");
    }
}
