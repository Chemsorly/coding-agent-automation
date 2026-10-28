using CodingAgent.Api.Client;
using CodingAgent.Api.Client.Stores;
using CodingAgent.Infrastructure.Common;
using CodingAgent.Orchestration;
using CodingAgent.Orchestration.Dispatch;
using System.Diagnostics.CodeAnalysis;
using System.Net.Http.Headers;
using CodingAgent.Orchestration.Redis;
using CodingAgent.Orchestration.Registry;
using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;
using CodingAgent.Pipeline.Services;
using CodingAgent.Kubernetes;
using CodingAgent.Pipeline.LeaderElection;
using CodingAgent.Scheduler.Services;
using k8s;
using Microsoft.Extensions.Http.Resilience;
using Serilog;
using StackExchange.Redis;

namespace CodingAgent.Scheduler;

/// <summary>
/// DI registration for all Scheduler services.
/// Called from Program.cs with the Pipeline API base URL, agent API key, and configuration.
/// </summary>
[ExcludeFromCodeCoverage(Justification = "Pure DI wiring — no unit-testable logic.")]
public static class SchedulerServiceCollectionExtensions
{
    private const string BearerScheme = "Bearer";

    public static IServiceCollection AddSchedulerServices(
        this IServiceCollection services,
        string pipelineApiBaseUrl,
        string agentApiKey,
        IConfiguration config)
    {
        services.AddSingleton(Log.Logger);

        // ── Shared HTTP resilience defaults ──────────────────────────────────
        // Sets PooledConnectionLifetime to 90s via AddStandardHttpClientPoolDefaults so
        // stale connections to replaced pod IPs are recycled after a rolling update.
        services.AddStandardHttpClientPoolDefaults();

        // ── HTTP clients to the API ──────────────────────────────────────────
        // CircuitBreaker.MinimumThroughput tuned to 10 (down from the default 100) so the
        // breaker can trip after ~10 failures in 30s. Without this, low-traffic clients
        // stall for the full 3×10s = 30s during a pod blip.
        Action<HttpClient> configurePipelineApiClient = c =>
        {
            c.BaseAddress = new Uri(pipelineApiBaseUrl);
            c.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue(BearerScheme, agentApiKey);
        };

        services.AddHttpClient<IPipelineApiConfigClient, PipelineApiConfigClient>(configurePipelineApiClient)
            .AddStandardResilienceHandler(PipelineApiClientServiceCollectionExtensions.ConfigureResilienceOptions);

        services.AddHttpClient<IPipelineApiWorkItemClient, PipelineApiWorkItemClient>(configurePipelineApiClient)
            .AddStandardResilienceHandler(PipelineApiClientServiceCollectionExtensions.ConfigureResilienceOptions);

        services.AddHttpClient<IPipelineApiRunHistoryClient, PipelineApiRunHistoryClient>(configurePipelineApiClient)
            .AddStandardResilienceHandler(PipelineApiClientServiceCollectionExtensions.ConfigureResilienceOptions);

        services.AddHttpClient("TokenVending")
            .AddStandardResilienceHandler(PipelineApiClientServiceCollectionExtensions.ConfigureResilienceOptions);

        // ── Store shims (moved to Api.Client.Stores in Spec 047) ─────────────
        AddApiStoreShims(services, config);

        // ── Redis (optional) ──────────────────────────────────────────────────
        var redisCs = config.GetValue<string>("SignalR:Redis:ConnectionString")
            ?? config.GetValue<string>("SignalR__Redis__ConnectionString");
        if (!string.IsNullOrEmpty(redisCs))
        {
            // Use a factory lambda so the connection is deferred until first resolution.
            // This prevents a RedisConnectionException crash during service registration
            // (before Build()) when Redis is temporarily unreachable at startup.
            // AbortOnConnectFail=false (set by RedisConnectionOptions.Parse) returns a
            // disconnected multiplexer that reconnects in the background.
            services.AddSingleton<IConnectionMultiplexer>(_ =>
            {
                var multiplexer = ConnectionMultiplexer.Connect(RedisConnectionOptions.Parse(redisCs));
                multiplexer.ConnectionFailed += (_, e) =>
                    Log.Warning("Scheduler Redis connection failed: {FailureType} — {Exception}",
                        e.FailureType, e.Exception?.Message);
                multiplexer.ConnectionRestored += (_, e) =>
                    Log.Information("Scheduler Redis connection restored: {EndPoint}", e.EndPoint);
                return multiplexer;
            });
            services.AddSingleton<IRedisStore>(sp =>
                new RedisStore(sp.GetRequiredService<IConnectionMultiplexer>().GetDatabase()));
        }

        // ── Leader election (Scheduler-specific K8s lease) ───────────────────
        services.AddSingleton<IKubernetes>(_ => CreateKubernetesClient()!);

        services.AddOptions<LeaderElectionOptions>()
            .Configure<IConfiguration>((opts, cfg) =>
            {
                cfg.GetSection(LeaderElectionOptions.SectionName).Bind(opts);
                // Default to scheduler-specific lease name so it doesn't conflict with API's lease
                if (string.IsNullOrEmpty(opts.LeaseName))
                    opts.LeaseName = "caa-scheduler-lock";
            })
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddSingleton<LeaderElectionService>(sp =>
            new LeaderElectionService(
                sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<LeaderElectionOptions>>(),
                sp.GetService<IKubernetes>()));
        services.AddSingleton<ILeaderElectionService>(sp => sp.GetRequiredService<LeaderElectionService>());
        services.AddHostedService(sp => sp.GetRequiredService<LeaderElectionService>());

        // ── Shared orchestration services (IProviderFactory, IConsolidationService,
        //    IDependencyChecker, IDispatchOrchestrationService, ConsolidationStalenessService) ──
        // Spec 051 2.3b, 2.3c, 2.4, 2.4b; issue #3592.
        // NOTE: IDispatchOrchestrationService is wired via IProviderConfigStore (not IConfigurationStore).
        // The Scheduler's IConfigurationStore registration (ApiConfigurationStore) is kept below for
        // other consumers — it is not the source for the third arg of DispatchOrchestrationServiceDependencies.
        services.AddSharedOrchestrationServices();

        // ── Token vending + label services ────────────────────────────────────
        services.AddTokenVendingCore();

        // ── Dispatch resolution ───────────────────────────────────────────────
        services.AddDispatchResolutionServices(includeWorkItemClient: true);

        // ── Work distributor (KubernetesWorkDistributor is already API-backed) ─
        services.AddSingleton<IWorkDistributor>(sp => new KubernetesWorkDistributor(
            sp.GetRequiredService<IPipelineApiWorkItemClient>(),
            sp.GetRequiredService<ILoggerFactory>().CreateLogger<KubernetesWorkDistributor>()));

        // ── Run lifecycle (HTTP-backed; no DB needed) ─────────────────────────
        services.AddSingleton<IPipelineRunHistoryService>(sp =>
            new ApiBackedPipelineRunHistoryService(
                sp.GetRequiredService<IPipelineApiRunHistoryClient>(),
                Log.Logger));

        // SchedulerRunQueryService provides the IRunActivityQuery the loop needs.
        // It overrides GetActiveRunBranchesAsync() to call the API instead of relying on
        // in-memory run state, so HousekeepingService's branch-update guard works correctly here.
        services.AddSingleton<SchedulerRunQueryService>(sp =>
            new SchedulerRunQueryService(sp.GetRequiredService<IPipelineApiRunHistoryClient>()));
        services.AddSingleton<IRunActivityQuery>(sp =>
            sp.GetRequiredService<SchedulerRunQueryService>());

        services.AddSingleton<PipelineRunLifecycleService>(sp => new PipelineRunLifecycleService(
            sp.GetRequiredService<IPipelineRunHistoryService>(),
            // TODO: PipelineRunLifecycleService receives null for IOrchestratorRunService because the
            // Scheduler has no run registry. The four named methods (CreateDispatchedRunAsync,
            // ReserveRunIdAsync, ReplaceDispatchedRun, ReleaseAgentRunsForHandoff) have no callers in
            // src at time of writing. If a Scheduler code path ever routes through IDispatchRunCreator
            // → PipelineRunLifecycleService → a method that dereferences _runService, run-registry
            // operations will be silently skipped (null-guarded), producing a history entry with no
            // corresponding active run. Audit PipelineRunLifecycleService members if new Scheduler
            // call paths are added.
            null, // no IOrchestratorRunService in the Scheduler — CreateDispatchedRunAsync etc. have no callers in src
            Log.Logger,
            agentCancellationSender: null)); // no hub in Scheduler

        services.AddSingleton<IDispatchRunCreator>(sp => new DispatchRunCreationService(
            sp.GetRequiredService<PipelineRunLifecycleService>()));

        // ── Housekeeping ──────────────────────────────────────────────────────
        services.AddSingleton<IStaleBranchCleaner>(_ => new StaleBranchCleaner(Log.Logger));
        services.AddSingleton<IIssueReworkService>(_ => new IssueReworkService(Log.Logger));
        services.AddSingleton<IHousekeepingService>(sp => new HousekeepingService(
            sp.GetRequiredService<IRunActivityQuery>(),
            sp.GetRequiredService<IStaleBranchCleaner>(),
            sp.GetRequiredService<IIssueReworkService>(),
            Log.Logger));

        // ── PipelineLoopService ───────────────────────────────────────────────
        services.AddSingleton<PipelineLoopServiceDependencies>(sp => new PipelineLoopServiceDependencies
        {
            Orchestration = sp.GetRequiredService<IDispatchRunCreator>(),
            ProviderFactory = sp.GetRequiredService<IProviderFactory>(),
            PipelineConfigStore = sp.GetRequiredService<IPipelineConfigStore>(),
            ProviderConfigStore = sp.GetRequiredService<IProviderConfigStore>(),
            ProjectStore = sp.GetRequiredService<IProjectStore>(),
            Logger = Log.Logger,
            WorkDistributor = sp.GetRequiredService<IWorkDistributor>(),
            DispatchOrchestration = sp.GetRequiredService<IDispatchOrchestrationService>(),
            DependencyChecker = sp.GetRequiredService<IDependencyChecker>(),
            HousekeepingService = sp.GetRequiredService<IHousekeepingService>(),
            LeaderElection = sp.GetService<ILeaderElectionService>(),
            WorkItemClient = sp.GetRequiredService<IPipelineApiWorkItemClient>(),
        });
        services.AddSingleton<PipelineLoopService>();
        services.AddSingleton<IPipelineLoopService>(sp => sp.GetRequiredService<PipelineLoopService>());
        services.AddHostedService(sp => sp.GetRequiredService<PipelineLoopService>());

        // ── LoopWatchdogService — reconciles the loop with the desired state (spec 051 D11) ──
        // Every 5 seconds on the leader: starts, stops or resumes the loop to match the pipeline
        // config that Start, Stop and Resume write, and pushes the loop status to the API. Starting
        // also self-heals a loop an exception or hang stopped, and is how the loop starts after a pod
        // start or a leadership change. A null leader gate (dev/single-replica
        // mode) causes the watchdog to treat this instance as the leader — matching WorkItemCountsService.
        services.AddSingleton<LoopWatchdogService>(sp => new LoopWatchdogService(
            sp.GetRequiredService<IPipelineLoopService>(),
            sp.GetRequiredService<IServiceScopeFactory>(),
            sp.GetService<ILeaderElectionService>(),
            Log.Logger,
            time: sp.GetService<TimeProvider>()));
        services.AddHostedService(sp => sp.GetRequiredService<LoopWatchdogService>());

        // ── OrphanedLabelRecoveryService ──────────────────────────────────────
        // Registered as a named singleton (in addition to IHostedService) so that
        // SchedulerE2EWebApplicationFactory can resolve it by concrete type for test hooks
        // (SweepOnceForTestAsync). Pattern mirrors LoopWatchdogService above.
        services.AddSingleton<OrphanedLabelRecoveryService>(sp => new OrphanedLabelRecoveryService(
            sp.GetRequiredService<IRunActivityQuery>(),
            sp.GetRequiredService<IServiceScopeFactory>(),
            sp.GetRequiredService<IProviderFactory>(),
            sp.GetRequiredService<ILabelService>(),
            sp.GetService<ILeaderElectionService>(),
            Log.Logger));
        services.AddHostedService(sp => sp.GetRequiredService<OrphanedLabelRecoveryService>());

        // ── FeedbackCommentRelayService ───────────────────────────────────────
        services.AddHttpClient<IPipelineApiFeedbackCommentOutboxClient, PipelineApiFeedbackCommentOutboxClient>(configurePipelineApiClient)
            .AddStandardResilienceHandler(PipelineApiClientServiceCollectionExtensions.ConfigureResilienceOptions);

        // Registered as a named singleton (in addition to IHostedService) so that
        // SchedulerE2EWebApplicationFactory can resolve it by concrete type for test hooks
        // (SweepOnceForTestAsync). Pattern mirrors LoopWatchdogService above.
        services.AddSingleton<FeedbackCommentRelayService>(sp => new FeedbackCommentRelayService(
            sp.GetRequiredService<IServiceScopeFactory>(),
            sp.GetRequiredService<IProviderFactory>(),
            sp.GetService<ILeaderElectionService>(),
            Log.Logger));
        services.AddHostedService(sp => sp.GetRequiredService<FeedbackCommentRelayService>());

        // ── Redis cleanup services (null-safe) ────────────────────────────────
        services.AddSingleton<AgentRegistryCleanupService>(sp =>
        {
            var store = sp.GetService<IRedisStore>();
            if (store is null) return null!;
            return new AgentRegistryCleanupService(store, Log.Logger,
                sp.GetService<ILeaderElectionService>());
        });
        services.AddSingleton<RunServiceCleanupService>(sp =>
        {
            var store = sp.GetService<IRedisStore>();
            if (store is null) return null!;
            return new RunServiceCleanupService(store, Log.Logger,
                sp.GetService<ILeaderElectionService>());
        });
        services.AddHostedService<AgentRegistryCleanupService>(sp =>
            sp.GetService<AgentRegistryCleanupService>()
            ?? new AgentRegistryCleanupService(new NullRedisStore(), Log.Logger));
        services.AddHostedService<RunServiceCleanupService>(sp =>
            sp.GetService<RunServiceCleanupService>()
            ?? new RunServiceCleanupService(new NullRedisStore(), Log.Logger));

        // ── Scheduler-specific background services ────────────────────────────
        // ISchedulerApiClient is registered as a typed HTTP client so that singletons that need it
        // can resolve a fresh instance per tick via IServiceScopeFactory.CreateAsyncScope(),
        // ensuring connection pools are recycled by IHttpClientFactory on the 90-second
        // PooledConnectionLifetime schedule after a Kubernetes rolling update.
        services.AddHttpClient<ISchedulerApiClient, HttpSchedulerApiClient>(configurePipelineApiClient)
            .AddStandardResilienceHandler(PipelineApiClientServiceCollectionExtensions.ConfigureResilienceOptions);

        services.AddSingleton<RetentionSweepSchedulerService>(sp =>
            new RetentionSweepSchedulerService(
                sp.GetRequiredService<IServiceScopeFactory>(),
                sp.GetService<ILeaderElectionService>(),
                Log.Logger));
        services.AddHostedService(sp => sp.GetRequiredService<RetentionSweepSchedulerService>());

        services.AddSingleton<AutoConsolidationService>(sp =>
            new AutoConsolidationService(
                sp.GetRequiredService<IConsolidationService>(),
                sp.GetRequiredService<ConsolidationStalenessService>(),
                sp.GetRequiredService<IPipelineConfigStore>(),
                sp.GetRequiredService<IServiceScopeFactory>(),
                sp.GetService<ILeaderElectionService>(),
                Log.Logger));
        services.AddHostedService(sp => sp.GetRequiredService<AutoConsolidationService>());

        services.AddSingleton<WorkItemCountsService>(sp =>
            new WorkItemCountsService(
                sp.GetRequiredService<IServiceScopeFactory>(),
                sp.GetService<ILeaderElectionService>(),
                Log.Logger));
        services.AddHostedService(sp => sp.GetRequiredService<WorkItemCountsService>());

        // ── WorkItemDispatchLoop — the sole dispatcher for Pending WorkItems ────────────────
        // Scheduler:Dispatch:Enabled=false exists only so test hosts can run without it.
        if (config.GetValue("Scheduler:Dispatch:Enabled", defaultValue: true))
        {
            var rateLimitPerSecond = config.GetValue("Scheduler:Dispatch:RateLimitPerSecond", defaultValue: 10);
            services.AddSingleton<WorkItemDispatchLoop>(sp =>
                new WorkItemDispatchLoop(
                    sp.GetRequiredService<IPipelineApiWorkItemClient>(),
                    sp.GetService<ILeaderElectionService>(),
                    Log.Logger,
                    rateLimitPerSecond: rateLimitPerSecond));
            services.AddHostedService(sp =>
                sp.GetRequiredService<WorkItemDispatchLoop>());
        }

        return services;
    }

    private static void AddApiStoreShims(IServiceCollection services, IConfiguration config)
    {
        var ttlSeconds = config.GetValue<int?>("PipelineLoop:ConfigCacheTtlSeconds");

        services.AddSingleton<ApiPipelineConfigStore>(sp =>
        {
            var store = new ApiPipelineConfigStore(sp.GetRequiredService<IPipelineApiConfigClient>());
            if (ttlSeconds is >= 0) store.CacheTtlSeconds = ttlSeconds.Value;
            return store;
        });
        services.AddSingleton<IPipelineConfigStore>(sp => sp.GetRequiredService<ApiPipelineConfigStore>());

        services.AddSingleton<ApiProviderConfigStore>(sp =>
        {
            var store = new ApiProviderConfigStore(sp.GetRequiredService<IPipelineApiConfigClient>());
            if (ttlSeconds is >= 0) store.CacheTtlSeconds = ttlSeconds.Value;
            return store;
        });
        services.AddSingleton<IProviderConfigStore>(sp => sp.GetRequiredService<ApiProviderConfigStore>());

        services.AddSingleton<ApiProjectStore>(sp =>
        {
            var store = new ApiProjectStore(sp.GetRequiredService<IPipelineApiConfigClient>());
            if (ttlSeconds is >= 0) store.CacheTtlSeconds = ttlSeconds.Value;
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
            if (ttlSeconds is >= 0) store.CacheTtlSeconds = ttlSeconds.Value;
            return store;
        });
        services.AddSingleton<IConfigurationStore>(sp => sp.GetRequiredService<ApiConfigurationStore>());
        services.AddSingleton<IQualityGateConfigStore>(sp => sp.GetRequiredService<ApiConfigurationStore>());
        services.AddSingleton<IReviewerConfigStore>(sp => sp.GetRequiredService<ApiConfigurationStore>());
    }

    private static IKubernetes? CreateKubernetesClient() =>
        KubernetesClientRegistration.TryCreate("Scheduler");
}
