using CodingAgentWebUI.Api.Client;
using CodingAgentWebUI.Infrastructure.Persistence;
using CodingAgentWebUI.Infrastructure.Persistence.Services;
using CodingAgentWebUI.Kubernetes;
using CodingAgentWebUI.Orchestration.Dispatch;
using CodingAgentWebUI.Services;
using CodingAgentWebUI.Orchestration.Registry;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.LeaderElection;
using k8s;
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
            RegisterPipelineApiClientFallback(services, configuration);
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
        // Map PipelineLoopLeaseName → LeaseName (Helm injects LeaderElection__PipelineLoopLeaseName)
        services.PostConfigure<LeaderElectionOptions>(opts =>
        {
            var leaseName = configuration.GetValue<string>($"{LeaderElectionOptions.SectionName}:PipelineLoopLeaseName");
            if (!string.IsNullOrEmpty(leaseName))
                opts.LeaseName = leaseName;
        });
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

        // ── IChatJobDispatcher — API-backed client ────────────────────────────
        // ChatJobDispatcher lives in the Pipeline API alongside AgentHub and the registry it polls.
        // ApiChatJobDispatcher delegates to POST /api/chat/dispatch and /terminate, re-mapping
        // HTTP status codes back to the domain exceptions AgentChat.razor expects.
        services.AddSingleton<IChatJobDispatcher, ApiChatJobDispatcher>();

        Log.Information("WorkDistribution: Kubernetes infrastructure registered (LeaderElection, K8s client)");
    }

    private static void RegisterPipelineApiClientFallback(IServiceCollection services, IConfiguration configuration)
    {
        var pipelineApiBaseUrl = configuration.GetValue<string>("PipelineApi:BaseUrl") ?? "";
        var agentApiKey = configuration.GetValue<string>("AGENT_API_KEY")
            ?? Environment.GetEnvironmentVariable("AGENT_API_KEY")
            ?? "";
        bool hasRequiredConfig = !string.IsNullOrEmpty(pipelineApiBaseUrl) && !string.IsNullOrEmpty(agentApiKey);
        if (hasRequiredConfig)
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
}
