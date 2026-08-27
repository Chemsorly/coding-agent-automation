using CodingAgentWebUI.Api.Client;
using CodingAgentWebUI.Kubernetes;
using CodingAgentWebUI.Orchestration.Dispatch;
using CodingAgentWebUI.Services;
using CodingAgentWebUI.Orchestration.Registry;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.LeaderElection;
using k8s;
using Serilog;

namespace CodingAgentWebUI;

public static partial class WorkDistributionRegistration
{
    /// <summary>
    /// Registers services for Kubernetes work distribution:
    /// - K8s infrastructure (IKubernetes for leader election)
    /// - Leader election (ILeaderElectionService)
    /// - IWorkDistributor (KubernetesWorkDistributor — fully API-backed, no EF)
    /// - JobTemplateStore
    /// - Pipeline API client
    ///
    /// IKubernetesJobClient and IJobCleanupStrategy are NOT registered here — they run
    /// in CodingAgentWebUI.Api (RunLifecycleManager → KubernetesJobCleanup). The monolith
    /// only needs IKubernetes for LeaderElectionService lease management.
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
        // Required by LeaderElectionService for Lease-based leader election.
        // IKubernetesJobClient is not registered here — job operations run in the API service.
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
        services.AddOptions<LeaderElectionOptions>()
            .Bind(configuration.GetSection(LeaderElectionOptions.SectionName))
            .PostConfigure(opts =>
            {
                // Map PipelineLoopLeaseName → LeaseName (Helm injects LeaderElection__PipelineLoopLeaseName)
                var leaseName = configuration.GetValue<string>($"{LeaderElectionOptions.SectionName}:PipelineLoopLeaseName");
                if (!string.IsNullOrEmpty(leaseName))
                    opts.LeaseName = leaseName;
            })
            .ValidateDataAnnotations()
            .ValidateOnStart();
        services.AddSingleton<LeaderElectionService>();
        services.AddSingleton<ILeaderElectionService>(sp => sp.GetRequiredService<LeaderElectionService>());
        services.AddHostedService(sp => sp.GetRequiredService<LeaderElectionService>());

        // ── IWorkDistributor (KubernetesWorkDistributor) ─────────────────────
        // KubernetesWorkDistributor is now fully API-backed — no IDbContextFactory,
        // no WorkItemTransitionService. Distribute, cancel, status, and dedup all
        // route through IPipelineApiWorkItemClient.
        services.AddSingleton<IWorkDistributor>(sp =>
        {
            var apiClient = sp.GetRequiredService<IPipelineApiWorkItemClient>();
            return new KubernetesWorkDistributor(
                apiClient,
                sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<KubernetesWorkDistributor>>());
        });

        // ── JobTemplateStore ─────────────────────────────────────────────────
        services.AddSingleton<JobTemplateStore>(sp =>
            JobTemplateProviderLoader.LoadTemplateProvider(sp.GetRequiredService<IConfiguration>()));

        // ── IChatJobDispatcher — API-backed client ────────────────────────────
        // ChatJobDispatcher lives in the Pipeline API alongside AgentHub and the registry it polls.
        // ApiChatJobDispatcher delegates to POST /api/chat/dispatch and /terminate, re-mapping
        // HTTP status codes back to the domain exceptions AgentChat.razor expects.
        services.AddSingleton<IChatJobDispatcher, ApiChatJobDispatcher>();

        Log.Information("WorkDistribution: Kubernetes infrastructure registered (LeaderElection, K8s client)");
    }

    [System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverage(
        Justification = "DI bootstrapping — only registers or logs a warning; " +
                        "no testable logic beyond environment variable reading.")]
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
