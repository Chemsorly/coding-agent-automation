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
    /// Registers services that survive the deletion of WorkDistributionRegistration.Kubernetes.cs:
    /// - K8s infrastructure (IKubernetes, IKubernetesJobClient, IJobCleanupStrategy)
    /// - Leader election (ILeaderElectionService)
    /// - IWorkDistributor (KubernetesWorkDistributor)
    /// - JobTemplateStore
    /// - IPendingWorkQuery — removed (Spec 045 Req 1.2, M1): dispatch.queue.depth gauge moved to API.
    ///   No PrometheusRule alerts reference dispatch.queue.depth, so removal is safe.
    /// - Pipeline API client
    ///
    /// NOTE: IJobCleanupStrategy (KubernetesJobCleanup) and IWorkDistributor (KubernetesWorkDistributor)
    /// still use IDbContextFactory and WorkItemTransitionService for cancel/status/dedup queries.
    /// These are registered in WorkDistributionRegistration.cs (the main AddWorkDistribution call).
    /// A future spec should migrate those to API calls (IPipelineApiWorkItemClient) and
    /// remove the remaining IDbContextFactory usage from the monolith.
    /// </summary>
    private static void RegisterConsolidationServices(IServiceCollection services, IConfiguration configuration)
    {
        // ── Pipeline API client ────────────────────────────────────────────────
        // NOTE: AddPipelineApiClient is now registered unconditionally in Program.cs (Spec 045 Task 2)
        // before AddWorkDistribution is called. The registration here is retained only as a
        // safety fallback for test environments that call AddWorkDistribution directly without
        // going through Program.cs. We use TryAdd-style guard: skip if already registered.
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
        // TODO(Spec 046): KubernetesJobCleanup still uses IDbContextFactory<PipelineDbContext> to
        // look up K8s Job names from WorkItems. Migrate to IPipelineApiWorkItemClient to complete
        // DB removal from the monolith. Tracked as a follow-up after Task 10.
        services.AddSingleton<IJobCleanupStrategy>(sp => new KubernetesJobCleanup(
            sp.GetRequiredService<IDbContextFactory<PipelineDbContext>>(),
            sp.GetRequiredService<IKubernetesJobClient>(),
            configuration.GetValue<string>("WorkDistribution:Namespace")
                ?? Environment.GetEnvironmentVariable("POD_NAMESPACE")
                ?? "default",
            Log.Logger));

        // ── IWorkDistributor (KubernetesWorkDistributor) ─────────────────────
        // Uses GetService (nullable) for IPipelineApiWorkItemClient since it may not be
        // registered in test environments where PipelineApi:BaseUrl is not configured.
        // TODO(Spec 046): DbWorkDistributorBase (base class) uses IDbContextFactory for
        // cancel/status/dedup queries. Migrate to IPipelineApiWorkItemClient to complete
        // DB removal from the monolith.
        services.AddSingleton<IWorkDistributor>(sp =>
        {
            var apiClient = sp.GetService<IPipelineApiWorkItemClient>();
            if (apiClient is null)
            {
                Log.Warning("WorkDistribution: IPipelineApiWorkItemClient not registered — KubernetesWorkDistributor will throw NullReferenceException on CreateAsync. " +
                            "This is expected in test environments without PipelineApi configured.");
            }
            return new KubernetesWorkDistributor(
                apiClient!,
                sp.GetRequiredService<IDbContextFactory<PipelineDbContext>>(),
                sp.GetRequiredService<WorkItemTransitionService>(),
                sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<KubernetesWorkDistributor>>());
        });

        // ── JobTemplateStore ─────────────────────────────────────────────────
        services.AddSingleton<JobTemplateStore>(sp =>
            DispatchService.LoadTemplateProvider(sp.GetRequiredService<IConfiguration>()));

        // ── IPendingWorkQuery — REMOVED in Spec 045 Req 1.2 (M1 gauge audit) ──────────────
        // dispatch.queue.depth was backed by DbPendingWorkQuery (IDbContextFactory).
        // No PrometheusRule alert references this metric name — removal is safe.
        // The gauge is removed from ObservableGaugeRegistrationExtensions.cs.
        // TODO(Spec 046): if dispatch queue depth monitoring is needed, implement via
        // GET /api/work-items/pending-count on IPipelineApiWorkItemClient.

        // ── ChatJobDispatcher — on-demand ephemeral chat pod dispatch ────────────
        // Spec 043 deleted WorkDistributionRegistration.Kubernetes.cs, which held this
        // registration, on the assumption that Spec 044 Task 6 would re-home it in the API.
        // That move never happened: ChatJobDispatcher still lives in CodingAgentWebUI.Hub and
        // is registered nowhere else, so AgentChat.razor's `@inject IChatJobDispatcher` threw
        // on first render. Restored here, matching the pre-043 wiring — the monolith retains
        // every dependency it needs (Req 044 keeps the Hub project reference and
        // AddSignalRServices() alive precisely for this).
        //
        // TODO(Spec 046): moving this to the API is still the intended end state, together with
        // the REST endpoint that fixes AgentChat's disconnected IHubContext<AgentHub> —
        // AssignChatPrompt cannot reach agents registered on the API hub from this process.
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
