using CodingAgent.Api.Client;
using CodingAgent.Infrastructure;
using CodingAgent.Infrastructure.Persistence.Services;
using CodingAgent.Kubernetes;
using CodingAgent.Orchestration.Dispatch;
using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Api.Client.Stores;
using Serilog;

namespace CodingAgent.Web;

/// <summary>
/// Registers work distribution services for Kubernetes deployment.
/// All persistence routes through the Pipeline API — no direct Postgres access in the orchestrator.
/// T8 complete (arch-audit 2026-08-22): IDbContextFactory removed from orchestrator registration.
/// </summary>
public static partial class WorkDistributionRegistration
{
    /// <summary>
    /// Registers work distribution services: API-backed stores, the work distributor and job templates.
    /// All persistence routes through Pipeline API — no IDbContextFactory in this method.
    /// </summary>
    public static IServiceCollection AddWorkDistribution(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        // ── IPipelineRunHistoryService — API-backed (T8 item 2) ─────────────
        // Orchestrator no longer writes run history directly to Postgres.
        // ApiBackedPipelineRunHistoryService routes AddRunToHistoryAsync/GetRunHistoryAsync
        // through POST|GET /api/pipeline-runs.
        services.AddSingleton<IPipelineRunHistoryService>(sp =>
            new CodingAgent.Api.Client.Stores.ApiBackedPipelineRunHistoryService(
                sp.GetRequiredService<IPipelineApiRunHistoryClient>(),
                Log.Logger));

        // ── Harness suggestions persistence — API-backed ────────────────────────────────────────
        services.AddSingleton<IHarnessSuggestionStore>(sp =>
            new ApiBackedHarnessSuggestionStore(sp.GetRequiredService<IPipelineApiHarnessSuggestionClient>()));

        // The following services were removed — registrations live in CodingAgent.Api:
        //   IActiveRunQueryService → IPipelineApiRunHistoryClient
        //   AnalysisStalenessDetector → deleted (B6, arch-audit 2026-08-22); was never registered
        //   PostgresConfigurationStore → API-backed adapters in ServiceCollectionExtensions.PipelineBackgroundServices
        //   ILoopStateStore → ClosedLoopAutoStart in PipelineConfiguration
        //   IKeyValueStore → IPipelineApiConfigClient
        //   WorkItemMetricsBackgroundService → CodingAgent.Api
        //   DatabaseMaintenanceService → CodingAgent.Api

        // ── Polly DB resilience pipelines — NOT registered in the Web host (Spec 048 Phase 2) ──
        // The "db-request"/"db-background" pipelines guard direct EF Core operations
        // (WorkItemTransitionService et al.), which run only in the API host. The Web host is
        // Persistence-free — all its data access is API-backed HTTP with its own
        // AddStandardResilienceHandler pipeline — so it has no consumer for these and no longer
        // references Infrastructure.Persistence where RegisterResiliencePipelines() is defined.

        // ── Work distributor, job templates and chat dispatch ─────────────────────────────────────
        RegisterConsolidationServices(services, configuration);

        // No SignalR backplane: the web host maps no hub of its own, and Blazor circuits are
        // pinned to one replica by sticky sessions. The hubs and their backplane live in the API.

        Log.Information("WorkDistribution: Kubernetes mode — all persistence routes through Pipeline API. " +
                        "No direct Postgres access in orchestrator. T8 complete.");

        return services;
    }
}
