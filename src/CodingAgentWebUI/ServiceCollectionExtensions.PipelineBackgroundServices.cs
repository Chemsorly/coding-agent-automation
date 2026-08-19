using CodingAgentWebUI.Api.Client;
using CodingAgentWebUI.Orchestration.Dispatch;
using CodingAgentWebUI.Pipeline.LeaderElection;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Pipeline.Services;
using CodingAgentWebUI.Services;
using Serilog;

namespace CodingAgentWebUI;

public static partial class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers pipeline background services: orphaned label recovery, dependency checker,
    /// pipeline loop service, and issue description parser.
    /// </summary>
    private static void RegisterPipelineBackgroundServices(IServiceCollection services)
    {
        services.AddHostedService(sp => new OrphanedLabelRecoveryService(
            sp.GetRequiredService<IOrchestratorRunService>(),
            sp.GetRequiredService<IPipelineApiConfigClient>(),
            sp.GetRequiredService<IProviderFactory>(),
            sp.GetRequiredService<ILabelService>(),
            Log.Logger));

        services.AddSingleton<IDependencyChecker>(sp => new DependencyChecker(Log.Logger));
        services.AddSingleton<IHousekeepingService>(sp => new HousekeepingService(
            sp.GetRequiredService<IOrchestratorRunService>(),
            Log.Logger));

        // Spec 045 Req 4.2 Option B — API-backed store adapters with TTL caching (Req 4.3).
        // These wrap IPipelineApiConfigClient to implement the store interfaces that
        // PipelineLoopServiceDependencies requires. The TTL prevents excessive API calls
        // during the tight polling loop. TTL is configurable via PipelineLoop:ConfigCacheTtlSeconds.
        services.AddSingleton<ApiPipelineConfigStore>(sp =>
        {
            var store = new ApiPipelineConfigStore(sp.GetRequiredService<IPipelineApiConfigClient>());
            var config = sp.GetService<IConfiguration>();
            var ttl = config?.GetValue<int>("PipelineLoop:ConfigCacheTtlSeconds");
            if (ttl.HasValue && ttl.Value >= 0) store.CacheTtlSeconds = ttl.Value;
            return store;
        });
        services.AddSingleton<IPipelineConfigStore>(sp => sp.GetRequiredService<ApiPipelineConfigStore>());
        services.AddSingleton<ApiProviderConfigStore>(sp =>
        {
            var store = new ApiProviderConfigStore(sp.GetRequiredService<IPipelineApiConfigClient>());
            var config = sp.GetService<IConfiguration>();
            var ttl = config?.GetValue<int>("PipelineLoop:ConfigCacheTtlSeconds");
            if (ttl.HasValue && ttl.Value >= 0) store.CacheTtlSeconds = ttl.Value;
            return store;
        });
        services.AddSingleton<IProviderConfigStore>(sp => sp.GetRequiredService<ApiProviderConfigStore>());
        services.AddSingleton<ApiProjectStore>(sp =>
        {
            var store = new ApiProjectStore(sp.GetRequiredService<IPipelineApiConfigClient>());
            var config = sp.GetService<IConfiguration>();
            var ttl = config?.GetValue<int>("PipelineLoop:ConfigCacheTtlSeconds");
            if (ttl.HasValue && ttl.Value >= 0) store.CacheTtlSeconds = ttl.Value;
            return store;
        });
        services.AddSingleton<IProjectStore>(sp => sp.GetRequiredService<ApiProjectStore>());

        // Spec 045: Register the composite IConfigurationStore backed by IPipelineApiConfigClient.
        // Required by LabelService, DispatchResolutionService, PipelineOrchestrationService,
        // ConsolidationJobPreparationService, ConsolidationDispatchService, and DrawerServices.
        // IConfigurationStore was PostgresConfigurationStore before Task 8 removed it.
        services.AddSingleton<ApiConfigurationStore>(sp =>
        {
            var store = new ApiConfigurationStore(sp.GetRequiredService<IPipelineApiConfigClient>());
            var config = sp.GetService<IConfiguration>();
            var ttl = config?.GetValue<int>("PipelineLoop:ConfigCacheTtlSeconds");
            if (ttl.HasValue && ttl.Value >= 0) store.CacheTtlSeconds = ttl.Value;
            return store;
        });
        services.AddSingleton<IConfigurationStore>(sp => sp.GetRequiredService<ApiConfigurationStore>());
        // Register all sub-interfaces as the same ApiConfigurationStore instance so callers that
        // resolve IAgentProfileStore or IQualityGateConfigStore get the same cached object.
        services.AddSingleton<IAgentProfileStore>(sp => sp.GetRequiredService<ApiConfigurationStore>());
        services.AddSingleton<IQualityGateConfigStore>(sp => sp.GetRequiredService<ApiConfigurationStore>());
        services.AddSingleton<IReviewerConfigStore>(sp => sp.GetRequiredService<ApiConfigurationStore>());

        // Spec 045: Register IDispatchOrchestrationService.
        // Used by IssueDrawerService, PrReviewDrawerService, EpicDrawerService for manual dispatch.
        // IDispatchOrchestrationService was removed from DI in Task 8's "REMOVED" comment but
        // the drawer services still constructor-inject it. Without this registration the DI
        // container fails to build in Development mode (ValidateOnBuild catches it).
        services.AddSingleton<IDispatchOrchestrationService>(sp => new DispatchOrchestrationService(
            new DispatchOrchestrationServiceDependencies(
                sp.GetRequiredService<DispatchInfrastructure>(),
                sp.GetRequiredService<IWorkDistributor>(),
                sp.GetRequiredService<IAgentProfileStore>(),
                sp.GetRequiredService<IConfigurationStore>(),
                sp.GetRequiredService<IPipelineConfigStore>()),
            Log.Logger));


        services.AddSingleton<PipelineLoopServiceDependencies>(sp => new PipelineLoopServiceDependencies
        {
            Orchestration = sp.GetRequiredService<IDispatchRunCreator>(),
            ProviderFactory = sp.GetRequiredService<IProviderFactory>(),
            // Spec 045 Req 4.2: API-backed adapters satisfy the store interfaces.
            // These delegate to IPipelineApiConfigClient with TTL caching (Req 4.3).
            PipelineConfigStore = sp.GetRequiredService<IPipelineConfigStore>(),
            ProviderConfigStore = sp.GetRequiredService<IProviderConfigStore>(),
            ProjectStore = sp.GetRequiredService<IProjectStore>(),
            Logger = Log.Logger,
            WorkDistributor = sp.GetService<IWorkDistributor>(),
            DispatchOrchestration = sp.GetService<IDispatchOrchestrationService>(),
            DependencyChecker = sp.GetRequiredService<IDependencyChecker>(),
            HousekeepingService = sp.GetRequiredService<IHousekeepingService>(),
            // GetService returns null when ILeaderElectionService is not registered (Legacy mode),
            // which causes the loop to run unconditionally as before. In K8s and SignalR+DB modes
            // ILeaderElectionService implements ILeaderGate and the loop is leader-gated.
            LeaderElection = sp.GetService<ILeaderElectionService>()
        });
        services.AddSingleton<PipelineLoopService>();
        services.AddSingleton<IPipelineLoopService>(sp => sp.GetRequiredService<PipelineLoopService>());
        services.AddHostedService(sp => sp.GetRequiredService<PipelineLoopService>());

        // Spec 045 Req 1.2 (F6 — LoopStatePersistenceService / ILoopStateStore):
        // Option B chosen: ILoopStateStore (PostgresLoopStateStore) registration removed.
        // Loop auto-start state is persisted in PipelineConfiguration.ClosedLoopAutoStart,
        // which is loaded from the Pipeline API at startup via AutoStartPipelineLoopAsync()
        // (Spec 045 Req 4.4). There is no per-pod loop-running state persistence —
        // LoopStatePersistenceService and its ILoopStateStore dependency are not registered
        // in the monolith. The LoopStatePersistenceService class and FileSystemLoopStateStore
        // remain in the codebase for use in tests and potential future non-Postgres deployment.

        services.AddTransient<IssueDescriptionParser>();
    }
}
