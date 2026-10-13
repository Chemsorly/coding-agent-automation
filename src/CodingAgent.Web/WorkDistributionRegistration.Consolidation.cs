using CodingAgent.Api.Client;
using CodingAgent.Kubernetes;
using CodingAgent.Orchestration.Dispatch;
using CodingAgent.Web.Services;
using CodingAgent.Orchestration.Registry;
using CodingAgent.Pipeline.Interfaces;
using Serilog;

namespace CodingAgent.Web;

public static partial class WorkDistributionRegistration
{
    /// <summary>
    /// Registers services for Kubernetes work distribution:
    /// - IWorkDistributor (KubernetesWorkDistributor — fully API-backed, no EF)
    /// - JobTemplateStore
    /// - Pipeline API client
    ///
    /// The web host makes no Kubernetes API calls: job operations run in CodingAgent.Api, and
    /// leader election runs in the Scheduler and the Job Controller.
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

        // ── IWorkDistributor (KubernetesWorkDistributor) ─────────────────────
        // KubernetesWorkDistributor is now fully API-backed — no IDbContextFactory,
        // no WorkItemTransitionService. Distribute, cancel, status, and dedup all
        // route through IPipelineApiWorkItemClient. All task types (including Consolidation)
        // are enqueued as Pending WorkItems via POST /api/work-items; the Scheduler's
        // WorkItemDispatchLoop picks them up with RunType tier ordering.
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

        Log.Information("WorkDistribution: work distributor and job templates registered");
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
