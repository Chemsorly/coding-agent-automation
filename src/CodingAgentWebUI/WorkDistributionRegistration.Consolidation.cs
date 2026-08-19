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
    /// - JobTemplateStore (used by ChatJobDispatcher)
    /// - DispatchLifecycleService + DispatchStateBuilder (used by DispatchService until Task 9.6)
    /// - IPendingWorkQuery (hard startup crash if missing — ObservableGaugeRegistrationExtensions)
    /// - ChatJobDispatcher (IHostedService — silent regression if missing)
    /// - DispatchService + ReconciliationService hosted services (deleted in Task 9.6)
    /// - Pipeline API client
    /// </summary>
    private static void RegisterConsolidationServices(IServiceCollection services, IConfiguration configuration)
    {
        // ── Pipeline API client ────────────────────────────────────────────────
        var pipelineApiBaseUrl = configuration.GetValue<string>("PipelineApi:BaseUrl") ?? "";
        var agentApiKey = configuration.GetValue<string>("AGENT_API_KEY")
            ?? Environment.GetEnvironmentVariable("AGENT_API_KEY")
            ?? "";
        if (string.IsNullOrEmpty(pipelineApiBaseUrl) || string.IsNullOrEmpty(agentApiKey))
        {
            Log.Warning("WorkDistribution: PipelineApi:BaseUrl or AGENT_API_KEY not configured — " +
                        "IPipelineApiWorkItemClient will not be registered. " +
                        "KubernetesWorkDistributor will fail at startup if PipelineApi is required.");
        }
        else
        {
            services.AddPipelineApiClient(new PipelineApiClientOptions
            {
                BaseUrl = pipelineApiBaseUrl,
                AgentApiKey = agentApiKey
            });
            Log.Information("WorkDistribution: Pipeline API client registered (BaseUrl={BaseUrl})", pipelineApiBaseUrl);
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

        // ── DispatchLifecycleService (Orchestration copy - used by ChatJobDispatcher's job creation path) ─
        // Note: After Task 9.6 deletes DispatchService.cs from Orchestration, this is no longer needed
        // for DispatchService, but ChatJobDispatcher still needs the Orchestration infrastructure for
        // its job creation via JobSpecBuilder. KubernetesJobClient handles that.
        // DispatchLifecycleService and DispatchStateBuilder are kept here in case
        // ChatJobDispatcher's dispatch path uses them transitively.
        // After spec 044 moves ChatJobDispatcher to the API, these can be removed from the monolith.

        // ── IPendingWorkQuery — MUST remain in the monolith ──────────────────
        // ObservableGaugeRegistrationExtensions.cs calls GetRequiredService<IPendingWorkQuery>()
        // unconditionally from Program.cs. Removing this registration causes a hard startup crash.
        services.AddSingleton<IPendingWorkQuery>(sp =>
            new DbPendingWorkQuery(sp.GetRequiredService<IDbContextFactory<PipelineDbContext>>()));

        // ── ChatJobDispatcher — MUST be re-registered ─────────────────────────
        // 042 moved the source file into CodingAgentWebUI.Hub.
        // The three DI lines lived in WorkDistributionRegistration.Kubernetes.cs (now deleted).
        // ChatJobDispatcher is an IHostedService — losing the registration is not a compile error;
        // agent chat just silently stops dispatching pods. Re-registration per Req 5.1b.
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

        Log.Information("WorkDistribution: Kubernetes infrastructure registered (LeaderElection, K8s client, ChatJobDispatcher)");
    }
}
