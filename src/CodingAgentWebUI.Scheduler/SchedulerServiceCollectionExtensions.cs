using CodingAgentWebUI.Api.Client;
using CodingAgentWebUI.Api.Client.Stores;
using CodingAgentWebUI.Infrastructure;
using CodingAgentWebUI.Orchestration;
using CodingAgentWebUI.Orchestration.Dispatch;
using System.Diagnostics.CodeAnalysis;
using CodingAgentWebUI.Orchestration.Redis;
using CodingAgentWebUI.Orchestration.Registry;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Pipeline.Services;
using CodingAgentWebUI.Kubernetes;
using CodingAgentWebUI.Pipeline.LeaderElection;
using CodingAgentWebUI.Scheduler.Services;
using k8s;
using Microsoft.Extensions.Http.Resilience;
using Serilog;
using StackExchange.Redis;

namespace CodingAgentWebUI.Scheduler;

/// <summary>
/// DI registration for all Scheduler services.
/// Called from Program.cs with the Pipeline API base URL, agent API key, and configuration.
/// </summary>
[ExcludeFromCodeCoverage(Justification = "Pure DI wiring — no unit-testable logic.")]
public static class SchedulerServiceCollectionExtensions
{
    public static IServiceCollection AddSchedulerServices(
        this IServiceCollection services,
        string pipelineApiBaseUrl,
        string agentApiKey,
        IConfiguration config)
    {
        services.AddSingleton(Log.Logger);

        // ── HTTP clients to the API ──────────────────────────────────────────
        services.AddHttpClient<IPipelineApiConfigClient, PipelineApiConfigClient>(c =>
        {
            c.BaseAddress = new Uri(pipelineApiBaseUrl);
            c.DefaultRequestHeaders.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", agentApiKey);
        }).AddStandardResilienceHandler();

        services.AddHttpClient<IPipelineApiWorkItemClient, PipelineApiWorkItemClient>(c =>
        {
            c.BaseAddress = new Uri(pipelineApiBaseUrl);
            c.DefaultRequestHeaders.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", agentApiKey);
        }).AddStandardResilienceHandler();

        services.AddHttpClient<IPipelineApiRunHistoryClient, PipelineApiRunHistoryClient>(c =>
        {
            c.BaseAddress = new Uri(pipelineApiBaseUrl);
            c.DefaultRequestHeaders.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", agentApiKey);
        }).AddStandardResilienceHandler();

        services.AddHttpClient("TokenVending")
            .AddStandardResilienceHandler();

        // ── Store shims (moved to Api.Client.Stores in Spec 047) ─────────────
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
        services.AddSingleton<IAgentProfileStore>(sp => sp.GetRequiredService<ApiConfigurationStore>());
        services.AddSingleton<IQualityGateConfigStore>(sp => sp.GetRequiredService<ApiConfigurationStore>());
        services.AddSingleton<IReviewerConfigStore>(sp => sp.GetRequiredService<ApiConfigurationStore>());

        // ── Redis (optional) ──────────────────────────────────────────────────
        var redisCs = config.GetValue<string>("SignalR:Redis:ConnectionString")
            ?? config.GetValue<string>("SignalR__Redis__ConnectionString");
        if (!string.IsNullOrEmpty(redisCs))
        {
            services.AddSingleton<IConnectionMultiplexer>(ConnectionMultiplexer.Connect(redisCs));
            services.AddSingleton<IRedisStore>(sp =>
                new RedisStore(sp.GetRequiredService<IConnectionMultiplexer>().GetDatabase()));
        }

        // ── Leader election (Scheduler-specific K8s lease) ───────────────────
        services.AddSingleton<IKubernetes>(_ =>
        {
            try
            {
                var inCluster = KubernetesClientConfiguration.IsInCluster();
                var k8sConfig = inCluster
                    ? KubernetesClientConfiguration.InClusterConfig()
                    : KubernetesClientConfiguration.BuildDefaultConfig();
                if (string.IsNullOrEmpty(k8sConfig.Host) || k8sConfig.Host == "http://localhost:8080")
                {
                    Log.Warning("Scheduler: Kubernetes client host is empty or localhost — K8s unavailable.");
                    return null!;
                }
                Log.Information("Scheduler: Kubernetes client configured ({Source})",
                    inCluster ? "in-cluster" : "kubeconfig");
                return new k8s.Kubernetes(k8sConfig);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Scheduler: Kubernetes client unavailable — leader election inactive");
                return null!;
            }
        });

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

        // ── Provider factory ──────────────────────────────────────────────────
        services.AddSingleton<IProviderFactory>(sp =>
            new ProviderFactory(sp.GetRequiredService<IPipelineConfigStore>()));

        // ── Token vending + label services ────────────────────────────────────
        services.AddSingleton<ITokenVendingService>(sp =>
            new TokenVendingService(Log.Logger, sp.GetRequiredService<IHttpClientFactory>()));

        services.AddSingleton<ILabelService>(sp => new LabelService(
            sp.GetRequiredService<IProviderConfigStore>(),
            sp.GetRequiredService<IProviderFactory>(),
            Log.Logger));

        // ── Dispatch resolution ───────────────────────────────────────────────
        services.AddDispatchResolutionServices(includeWorkItemClient: true);

        // ── Work distributor (KubernetesWorkDistributor is already API-backed) ─
        services.AddSingleton<IWorkDistributor>(sp => new KubernetesWorkDistributor(
            sp.GetRequiredService<IPipelineApiWorkItemClient>(),
            sp.GetRequiredService<ILoggerFactory>().CreateLogger<KubernetesWorkDistributor>()));

        // ── Dispatch orchestration service ────────────────────────────────────
        services.AddSingleton<IDispatchOrchestrationService>(sp => new DispatchOrchestrationService(
            new DispatchOrchestrationServiceDependencies(
                sp.GetRequiredService<DispatchInfrastructure>(),
                sp.GetRequiredService<IWorkDistributor>(),
                sp.GetRequiredService<IAgentProfileStore>(),
                sp.GetRequiredService<IConfigurationStore>(),
                sp.GetRequiredService<IPipelineConfigStore>()),
            Log.Logger));

        // ── Run lifecycle (HTTP-backed; no DB needed) ─────────────────────────
        services.AddSingleton<IPipelineRunHistoryService>(sp =>
            new ApiBackedPipelineRunHistoryService(
                sp.GetRequiredService<IPipelineApiRunHistoryClient>(),
                Log.Logger));

        // SchedulerRunQueryService provides the IOrchestratorRunService the loop needs.
        // Read-only — active runs always empty until API exposes BranchName (see SchedulerRunQueryService).
        services.AddSingleton<SchedulerRunQueryService>();
        services.AddSingleton<IOrchestratorRunService>(sp =>
            sp.GetRequiredService<SchedulerRunQueryService>());

        services.AddSingleton<PipelineRunLifecycleService>(sp => new PipelineRunLifecycleService(
            sp.GetRequiredService<IPipelineRunHistoryService>(),
            sp.GetRequiredService<IOrchestratorRunService>(),
            Log.Logger,
            agentCancellationSender: null)); // no hub in Scheduler

        services.AddSingleton<IDispatchRunCreator>(sp => new DispatchRunCreationService(
            sp.GetRequiredService<PipelineRunLifecycleService>(),
            sp.GetRequiredService<IProviderConfigStore>(),
            sp.GetRequiredService<IProviderFactory>(),
            Log.Logger));

        // ── Dependency checker ────────────────────────────────────────────────
        services.AddSingleton<IDependencyChecker>(_ => new DependencyChecker(Log.Logger));

        // ── Housekeeping ──────────────────────────────────────────────────────
        services.AddSingleton<IHousekeepingService>(sp => new HousekeepingService(
            sp.GetRequiredService<IOrchestratorRunService>(),
            Log.Logger));

        // ── PipelineLoopService ───────────────────────────────────────────────
        services.AddSingleton<PipelineLoopServiceDependencies>(sp => new PipelineLoopServiceDependencies
        {
            Orchestration         = sp.GetRequiredService<IDispatchRunCreator>(),
            ProviderFactory       = sp.GetRequiredService<IProviderFactory>(),
            PipelineConfigStore   = sp.GetRequiredService<IPipelineConfigStore>(),
            ProviderConfigStore   = sp.GetRequiredService<IProviderConfigStore>(),
            ProjectStore          = sp.GetRequiredService<IProjectStore>(),
            Logger                = Log.Logger,
            WorkDistributor       = sp.GetRequiredService<IWorkDistributor>(),
            DispatchOrchestration = sp.GetRequiredService<IDispatchOrchestrationService>(),
            DependencyChecker     = sp.GetRequiredService<IDependencyChecker>(),
            HousekeepingService   = sp.GetRequiredService<IHousekeepingService>(),
            LeaderElection        = sp.GetService<ILeaderElectionService>(),
        });
        services.AddSingleton<PipelineLoopService>();
        services.AddSingleton<IPipelineLoopService>(sp => sp.GetRequiredService<PipelineLoopService>());
        services.AddHostedService(sp => sp.GetRequiredService<PipelineLoopService>());

        // Cache singleton for the /loop/status handler — scoped to this DI container rather than
        // a static field, so integration tests with multiple WebApplication instances don't share state.
        services.AddSingleton<LoopStatusCache>();

        // ── OrphanedLabelRecoveryService ──────────────────────────────────────
        services.AddHostedService(sp => new OrphanedLabelRecoveryService(
            sp.GetRequiredService<IOrchestratorRunService>(),
            sp.GetRequiredService<IPipelineApiConfigClient>(),
            sp.GetRequiredService<IPipelineApiWorkItemClient>(),
            sp.GetRequiredService<IProviderFactory>(),
            sp.GetRequiredService<ILabelService>(),
            sp.GetService<ILeaderElectionService>(),
            Log.Logger));

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
        services.AddSingleton<ISchedulerApiClient>(sp =>
        {
            // RetentionSweepSchedulerService and WorkItemCountsPoller call the API (not the Scheduler).
            // Register an HttpClient pointing to the Pipeline API base URL.
            var factory = sp.GetRequiredService<IHttpClientFactory>();
            var httpClient = factory.CreateClient("SchedulerToApi");
            httpClient.BaseAddress = new Uri(pipelineApiBaseUrl);
            httpClient.DefaultRequestHeaders.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", agentApiKey);
            return new HttpSchedulerApiClient(httpClient);
        });

        services.AddHttpClient("SchedulerToApi")
            .AddStandardResilienceHandler();

        services.AddSingleton<RetentionSweepSchedulerService>(sp =>
            new RetentionSweepSchedulerService(
                sp.GetRequiredService<ISchedulerApiClient>(),
                sp.GetService<ILeaderElectionService>(),
                Log.Logger));
        services.AddHostedService(sp => sp.GetRequiredService<RetentionSweepSchedulerService>());

        services.AddSingleton<WorkItemCountsPoller>(sp =>
            new WorkItemCountsPoller(
                sp.GetRequiredService<ISchedulerApiClient>(),
                sp.GetService<ILeaderElectionService>(),
                Log.Logger));
        services.AddHostedService(sp => sp.GetRequiredService<WorkItemCountsPoller>());

        return services;
    }
}
