using CodingAgent.Api.Client;
using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Api.Client.Stores;
using CodingAgent.Web.Services;
using Serilog;

namespace CodingAgent.Web;

public static partial class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the WebUI pipeline background services.
    ///
    /// Spec 047: PipelineLoopService, HousekeepingService, OrphanedLabelRecoveryService,
    /// IDependencyChecker, PipelineLoopServiceDependencies, and IPipelineLoopService have been
    /// moved to CodingAgent.Scheduler.
    ///
    /// RETAINED (consumed by the label service and the pages not yet on /api/ui):
    /// - ApiConfigurationStore + IConfigurationStore + IAgentProfileStore +
    ///   IQualityGateConfigStore + IReviewerConfigStore
    ///   (sourced from CodingAgent.Api.Client.Stores)
    /// Manual dispatch from the drawers runs in the API since spec 051 2.3c. The loop status and
    /// controls come from <c>/api/ui/loop</c> (spec 051 D11), so the web calls no Scheduler.
    /// </summary>
    private static void RegisterPipelineBackgroundServices(IServiceCollection services)
    {
        // ── Shared HTTP resilience defaults ──────────────────────────────────
        // Sets PooledConnectionLifetime so stale connections to replaced pod IPs (after a
        // rolling update) are recycled within 90s rather than persisting indefinitely.
        // Applied via ConfigureHttpClientDefaults so it covers every client registered in
        // this container.
        // ConfigureHttpClientDefaults is additive — if AddPipelineApiClient also registered a
        // ConfigurePrimaryHttpMessageHandler, the last registration wins. Both register an
        // identical SocketsHttpHandler factory so the outcome is correct regardless of order.
        // TODO: consolidate multiple ConfigureHttpClientDefaults registrations across this
        // container into a single shared call to avoid stacked identical handler factories.
        // See review finding [WARNING] ApiServiceCollectionExtensions.cs:220.
        services.ConfigureHttpClientDefaults(b =>
        {
            b.ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
            {
                PooledConnectionLifetime = TimeSpan.FromSeconds(90)
            });
        });

        // ── Spec 045 retained registrations ──────────────────────────────────────────────────
        // ApiConfigurationStore composed from three narrow store shims. Required by
        // LabelService and the pages not yet on /api/ui.

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
    }

    private static int? ResolveConfigCacheTtl(IServiceProvider sp)
    {
        var ttl = sp.GetService<IConfiguration>()?.GetValue<int>(ConfigCacheTtlKey);
        return ttl is >= 0 ? ttl : null;
    }

    private const string ConfigCacheTtlKey = "PipelineLoop:ConfigCacheTtlSeconds";
}
