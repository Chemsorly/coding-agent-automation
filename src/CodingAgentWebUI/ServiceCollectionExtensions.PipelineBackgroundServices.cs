using CodingAgentWebUI.Api.Client;
using CodingAgentWebUI.Orchestration.Dispatch;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Pipeline.Services;
using CodingAgentWebUI.Api.Client.Stores;
using CodingAgentWebUI.Services;
using Serilog;

namespace CodingAgentWebUI;

public static partial class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the WebUI pipeline background services.
    ///
    /// Spec 047: PipelineLoopService, HousekeepingService, OrphanedLabelRecoveryService,
    /// IDependencyChecker, PipelineLoopServiceDependencies, and IPipelineLoopService have been
    /// moved to CodingAgentWebUI.Scheduler.
    ///
    /// RETAINED (consumed by drawer services and DispatchOrchestrationService):
    /// - ApiConfigurationStore + IConfigurationStore + IAgentProfileStore +
    ///   IQualityGateConfigStore + IReviewerConfigStore
    ///   (sourced from CodingAgentWebUI.Api.Client.Stores)
    /// - IDispatchOrchestrationService
    ///
    /// ADDED (Spec 047):
    /// - ILoopStatusService / LoopStatusPollingService (polls Scheduler /loop/status)
    /// - ISchedulerApiClient / HttpSchedulerApiClient (calls Scheduler loop endpoints)
    /// </summary>
    private static void RegisterPipelineBackgroundServices(IServiceCollection services)
    {
        // ── Spec 047: Loop status polling (replaces in-process PipelineLoopService) ──────────
        // Polls GET /loop/status on the Scheduler every 3 seconds (configurable via
        // SchedulerApi:StatusPollIntervalSeconds). The Overview and Pipelines pages inject
        // ILoopStatusService instead of IPipelineLoopService — same read-only surface.
        // Register the concrete type as a singleton first so the hosted service and the
        // ILoopStatusService alias both share the same instance. E2E tests replace only
        // ILoopStatusService (with FakeLoopStatusService) — the hosted service registration
        // below targets the concrete type directly and is not affected by that replacement.
        services.AddSingleton<LoopStatusPollingService>(sp =>
        {
            var client = sp.GetRequiredService<ISchedulerApiClient>();
            var cfg = sp.GetService<IConfiguration>();
            var intervalSec = cfg?.GetValue<int?>("SchedulerApi:StatusPollIntervalSeconds");
            var interval = intervalSec is > 0 ? TimeSpan.FromSeconds(intervalSec.Value) : (TimeSpan?)null;
            return new LoopStatusPollingService(client, Log.Logger, interval);
        });
        services.AddSingleton<ILoopStatusService>(sp => sp.GetRequiredService<LoopStatusPollingService>());
        services.AddHostedService(sp => sp.GetRequiredService<LoopStatusPollingService>());

        // ── Spec 047: ISchedulerApiClient calls the Scheduler's loop control endpoints ────────
        services.AddHttpClient<ISchedulerApiClient, HttpSchedulerApiClient>(c =>
        {
            // Base URL must be configured via SchedulerApi__BaseUrl env var.
            // Injected via IHttpClientFactory and configured in AddHttpClient factory.
        }).ConfigureHttpClient((sp, c) =>
        {
            var cfg = sp.GetRequiredService<IConfiguration>();
            var baseUrl = cfg.GetValue<string>("SchedulerApi__BaseUrl")
                ?? cfg.GetValue<string>("SchedulerApi:BaseUrl")
                ?? "http://localhost:8080";

            if (baseUrl == "http://localhost:8080")
                Serilog.Log.Warning(
                    "SchedulerApi__BaseUrl is not configured — using localhost:8080 fallback. " +
                    "This is unreachable from WebUI pods in a Kubernetes deployment. " +
                    "Set SchedulerApi__BaseUrl to the Scheduler service URL.");

            c.BaseAddress = new Uri(baseUrl);
            var apiKey = cfg.GetValue<string>("AGENT_API_KEY")
                ?? cfg.GetValue<string>("AgentApiKey");
            if (!string.IsNullOrEmpty(apiKey))
                c.DefaultRequestHeaders.Add("X-Api-Key", apiKey);
        })
        .AddStandardResilienceHandler();

        // ── Spec 045 retained registrations (consumed by drawer services) ──────────────────
        // ApiConfigurationStore composed from three narrow store shims. Required by
        // LabelService, DispatchResolutionService, IssueDrawerService, PrReviewDrawerService,
        // EpicDrawerService, and ConsolidationJobPreparationService.

        services.AddSingleton<ApiPipelineConfigStore>(sp =>
        {
            var store = new ApiPipelineConfigStore(sp.GetRequiredService<IPipelineApiConfigClient>());
            if (ResolveConfigCacheTtl(sp) is { } ttl) store.CacheTtlSeconds = ttl;
            return store;
        });
        services.AddSingleton<IPipelineConfigStore>(sp => sp.GetRequiredService<ApiPipelineConfigStore>());
        services.AddSingleton<ApiProviderConfigStore>(sp =>
        {
            var store = new ApiProviderConfigStore(sp.GetRequiredService<IPipelineApiConfigClient>());
            if (ResolveConfigCacheTtl(sp) is { } ttl) store.CacheTtlSeconds = ttl;
            return store;
        });
        services.AddSingleton<IProviderConfigStore>(sp => sp.GetRequiredService<ApiProviderConfigStore>());
        services.AddSingleton<ApiProjectStore>(sp =>
        {
            var store = new ApiProjectStore(sp.GetRequiredService<IPipelineApiConfigClient>());
            if (ResolveConfigCacheTtl(sp) is { } ttl) store.CacheTtlSeconds = ttl;
            return store;
        });
        services.AddSingleton<IProjectStore>(sp => sp.GetRequiredService<ApiProjectStore>());

        services.AddSingleton<ApiConfigurationStore>(sp =>
        {
            var store = new ApiConfigurationStore(
                sp.GetRequiredService<IPipelineApiConfigClient>(),
                sp.GetRequiredService<ApiPipelineConfigStore>(),
                sp.GetRequiredService<ApiProviderConfigStore>(),
                sp.GetRequiredService<ApiProjectStore>());
            if (ResolveConfigCacheTtl(sp) is { } ttl) store.CacheTtlSeconds = ttl;
            return store;
        });
        services.AddSingleton<IConfigurationStore>(sp => sp.GetRequiredService<ApiConfigurationStore>());
        services.AddSingleton<IAgentProfileStore>(sp => sp.GetRequiredService<ApiConfigurationStore>());
        services.AddSingleton<IQualityGateConfigStore>(sp => sp.GetRequiredService<ApiConfigurationStore>());
        services.AddSingleton<IReviewerConfigStore>(sp => sp.GetRequiredService<ApiConfigurationStore>());

        // IDispatchOrchestrationService — retained for drawer services.
        // AgentCodingPageService now uses ISchedulerApiClient for loop controls; it no longer
        // takes IPipelineLoopService. IDispatchOrchestrationService is still needed for
        // manual dispatch via the issue/PR/epic drawers.
        services.AddSingleton<IDispatchOrchestrationService>(sp => new DispatchOrchestrationService(
            new DispatchOrchestrationServiceDependencies(
                sp.GetRequiredService<DispatchInfrastructure>(),
                sp.GetRequiredService<IWorkDistributor>(),
                sp.GetRequiredService<IAgentProfileStore>(),
                sp.GetRequiredService<IConfigurationStore>(),
                sp.GetRequiredService<IPipelineConfigStore>()),
            Log.Logger));

        services.AddTransient<IssueDescriptionParser>();

        // IDependencyChecker — needed by IssueDrawerService for pre-dispatch dependency checking.
        // Was incorrectly removed in Spec 047 (listed as "exclusively used by the loop" but the
        // drawer also uses it). Stateless implementation — safe as a singleton.
        services.AddSingleton<IDependencyChecker>(_ => new DependencyChecker(Log.Logger));
    }

    private static int? ResolveConfigCacheTtl(IServiceProvider sp)
    {
        var ttl = sp.GetService<IConfiguration>()?.GetValue<int>(ConfigCacheTtlKey);
        return ttl is >= 0 ? ttl : null;
    }

    private const string ConfigCacheTtlKey = "PipelineLoop:ConfigCacheTtlSeconds";
}
