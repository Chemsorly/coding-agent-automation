using CodingAgentWebUI.Hub;
using CodingAgentWebUI.Api.Client;
using CodingAgentWebUI.Infrastructure;
using CodingAgentWebUI.Infrastructure.Locking;using CodingAgentWebUI.Infrastructure.Persistence;
using CodingAgentWebUI.Infrastructure.Persistence.Services;
using CodingAgentWebUI.Infrastructure.Persistence.Stores;
using CodingAgentWebUI.Kubernetes;
using CodingAgentWebUI.Orchestration;
using CodingAgentWebUI.Orchestration.Dispatch;
using CodingAgentWebUI.Orchestration.Health;
using CodingAgentWebUI.Orchestration.Registry;
using CodingAgentWebUI.Orchestration.Telemetry;
using CodingAgentWebUI.Pipeline.Telemetry;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Pipeline.Services;
using k8s;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using Polly.Registry;
using Serilog;

namespace CodingAgentWebUI.Api;

/// <summary>
/// DI extension methods for CodingAgentWebUI.Api.
/// </summary>
public static class ApiServiceCollectionExtensions
{
    /// <summary>
    /// Normalizes the connection string: enforces Timeout >= 15 and SslMode=Require in production.
    /// Inlined from <c>DatabaseReadinessMonitor.NormalizeConnectionString</c> (monolith-only type).
    /// </summary>
    private static string NormalizeConnectionString(string connectionString, bool isProduction)
    {
        var csb = new NpgsqlConnectionStringBuilder(connectionString);
        if (csb.Timeout == 0)
            csb.Timeout = 15;
        if (isProduction && csb.SslMode == SslMode.Prefer)
            csb.SslMode = SslMode.Require;
        return csb.ConnectionString;
    }
    /// <summary>
    /// Registers infrastructure services for the Pipeline API:
    /// EF pooled factory, distributed lock, resilience pipelines, config store,
    /// run history, consolidation/harness/loop-state stores, key-value store.
    /// </summary>
    public static IServiceCollection AddApiInfrastructure(
        this IServiceCollection services,
        string connectionString)
    {
        // Normalize connection string (Timeout=15, SslMode=Require for production)
        var isProduction = !string.Equals(
            Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT"),
            "Development",
            StringComparison.OrdinalIgnoreCase);
        var normalizedConnectionString = NormalizeConnectionString(connectionString, isProduction);

        // ── EF Core DbContext Factory + scoped accessor ─────────────────────
        services.AddPooledDbContextFactory<PipelineDbContext>(opts =>
            opts.UseNpgsql(normalizedConnectionString));
        services.AddScoped(sp =>
            sp.GetRequiredService<IDbContextFactory<PipelineDbContext>>().CreateDbContext());

        // ── Distributed lock provider (Postgres advisory locks) ─────────────
        services.AddDistributedLockProvider(connectionString);

        // ── Polly resilience pipelines ──────────────────────────────────────
        services.RegisterResiliencePipelines();

        // ── WorkItemTransitionService ───────────────────────────────────────
        services.AddSingleton<WorkItemTransitionService>(sp => new WorkItemTransitionService(
            sp.GetRequiredService<IDbContextFactory<PipelineDbContext>>(),
            sp.GetRequiredService<ILoggerFactory>().CreateLogger<WorkItemTransitionService>(),
            sp.GetService<ResiliencePipelineProvider<string>>()));

        // ── WorkItemFallbackTransitionService ───────────────────────────────
        services.AddSingleton<IWorkItemFallbackTransitionService>(sp => new WorkItemFallbackTransitionService(
            sp.GetRequiredService<WorkItemTransitionService>(),
            sp.GetRequiredService<ILoggerFactory>().CreateLogger<WorkItemFallbackTransitionService>()));

        // ── PostgresConfigurationStore with cache DISABLED (Req 5.6b) ──────
        // Cache is disabled via a negative TTL sentinel so two processes don't serve stale config.
        // The store skips _cache.Set when _cacheTtl <= TimeSpan.Zero.
        services.AddSingleton<IConfigurationStore>(sp =>
            new PostgresConfigurationStore(
                sp.GetRequiredService<IDbContextFactory<PipelineDbContext>>(),
                cacheTtl: TimeSpan.FromTicks(-1)));
        services.RegisterConfigStoreSubInterfaces();

        // ── IPipelineRunHistoryService ──────────────────────────────────────
        services.AddSingleton<IPipelineRunHistoryService>(sp =>
            new PostgresPipelineRunHistoryService(
                sp.GetRequiredService<IDbContextFactory<PipelineDbContext>>(),
                Log.Logger));

        // ── IConsolidationRunStore ──────────────────────────────────────────
        services.AddSingleton<IConsolidationRunStore>(sp =>
            new PostgresConsolidationRunStore(sp.GetRequiredService<IDbContextFactory<PipelineDbContext>>()));

        // ── ILoopStateStore ─────────────────────────────────────────────────
        services.AddSingleton<ILoopStateStore>(sp =>
            new PostgresLoopStateStore(sp.GetRequiredService<IDbContextFactory<PipelineDbContext>>()));

        // ── IHarnessSuggestionStore ─────────────────────────────────────────
        services.AddSingleton<IHarnessSuggestionStore>(sp =>
            new PostgresHarnessSuggestionStore(sp.GetRequiredService<IDbContextFactory<PipelineDbContext>>()));

        // ── IKeyValueStore ──────────────────────────────────────────────────
        services.AddScoped<IKeyValueStore, EfKeyValueStore>();

        // ── IDatabaseProbe (no-op — real DB connectivity is handled by DatabaseStartupService) ─
        services.AddSingleton<IDatabaseProbe, NoOpDatabaseProbe>();

        // ── DatabaseHealthState + DatabaseReadinessMonitor ──────────────────
        // Registers the singleton health state that /readyz reads, and the background monitor
        // that probes DB every 5s and updates it. Connection string resolved from configuration.
        services.AddSingleton<DatabaseHealthState>();
        services.AddSingleton<DatabaseReadinessMonitor>(sp =>
        {
            var cfg = sp.GetRequiredService<IConfiguration>();
            var connStr = DatabaseConnectionResolver.Resolve(cfg) ?? "";
            return new DatabaseReadinessMonitor(
                sp.GetRequiredService<DatabaseHealthState>(),
                connStr,
                Log.Logger);
        });
        services.AddHostedService(sp => sp.GetRequiredService<DatabaseReadinessMonitor>());

        // ── TimeProvider ────────────────────────────────────────────────────
        services.AddSingleton(TimeProvider.System);

        return services;
    }

    /// <summary>
    /// Registers orchestration services needed by the hub graph:
    /// agent registry, run service, job deduplication, dispatch infrastructure,
    /// lifecycle manager, label/token/consolidation services, and agent communication.
    /// </summary>
    public static IServiceCollection AddApiOrchestration(this IServiceCollection services)
    {
        // Serilog.ILogger for DI resolution (some services take Serilog.ILogger directly)
        services.AddSingleton(Log.Logger);

        // ── IProviderFactory (not registered by Infrastructure — must be explicit) ──
        services.AddSingleton<IProviderFactory>(sp =>
            new ProviderFactory(sp.GetRequiredService<IPipelineConfigStore>()));

        // ── AgentRegistryService + IAgentRegistryService ────────────────────
        // Use DistributedAgentRegistryService when Redis is available (multi-replica mode);
        // fall back to in-memory AgentRegistryService for local dev without Redis.
        services.AddSingleton<AgentRegistryService>();
        services.AddSingleton<IAgentRegistryService>(sp =>
        {
            var mux = sp.GetService<StackExchange.Redis.IConnectionMultiplexer>();
            if (mux is not null)
            {
                var store = new CodingAgentWebUI.Orchestration.Redis.RedisStore(mux.GetDatabase());
                Log.Information("AgentRegistry: distributed (Redis)");
                return new DistributedAgentRegistryService(store, Log.Logger);
            }
            Log.Information("AgentRegistry: in-memory (local development — Redis not configured)");
            return sp.GetRequiredService<AgentRegistryService>();
        });

        // ── OrchestratorRunService + IOrchestratorRunService ────────────────
        // Use DistributedRunService when Redis is available; fall back to in-memory.
        services.AddSingleton<OrchestratorRunService>(sp => new OrchestratorRunService(Log.Logger));
        services.AddSingleton<IOrchestratorRunService>(sp =>
        {
            var mux = sp.GetService<StackExchange.Redis.IConnectionMultiplexer>();
            if (mux is not null)
            {
                var store = new CodingAgentWebUI.Orchestration.Redis.RedisStore(mux.GetDatabase());
                // IsIssueBeingProcessed: direct Postgres query — no HTTP self-call needed.
                var dbFactory = sp.GetRequiredService<IDbContextFactory<PipelineDbContext>>();
                var activeStatuses = PipelineConstants.ActiveWorkItemStatuses;
                var cooldown = PipelineConstants.DefaultRestartDedupCooldown;
                Func<string, string, CancellationToken, Task<bool>> isIssueDistributed =
                    async (issueId, providerConfigId, ct) =>
                    {
                        await using var db = await dbFactory.CreateDbContextAsync(ct);
                        // Mirror WorkItemEndpoints.GetIsDistributed: active status OR recently completed.
                        var hasActive = await db.WorkItems.AsNoTracking().AnyAsync(w =>
                            w.IssueIdentifier == issueId &&
                            w.IssueProviderConfigId == providerConfigId &&
                            activeStatuses.Contains(w.Status), ct);
                        if (hasActive) return true;
                        var since = DateTimeOffset.UtcNow - cooldown;
                        return await db.WorkItems.AsNoTracking().AnyAsync(w =>
                            w.IssueIdentifier == issueId &&
                            w.IssueProviderConfigId == providerConfigId &&
                            !activeStatuses.Contains(w.Status) &&
                            w.CompletedAt != null &&
                            w.CompletedAt >= since, ct);
                    };
                return new DistributedRunService(store, isIssueDistributed, Log.Logger);
            }
            return sp.GetRequiredService<OrchestratorRunService>();
        });

        // ── AgentReservationService (renamed from JobDeduplicationGuardService) ────────
        services.AddSingleton<AgentReservationService>(sp =>
        {
            var mux = sp.GetService<StackExchange.Redis.IConnectionMultiplexer>();
            CodingAgentWebUI.Orchestration.Redis.IRedisStore? store = mux is not null
                ? new CodingAgentWebUI.Orchestration.Redis.RedisStore(mux.GetDatabase())
                : null;
            return new AgentReservationService(sp.GetRequiredService<IAgentRegistryService>(), Log.Logger, store);
        });
        // Backward-compat: JobDeduplicationGuardService resolves to AgentReservationService
        services.AddSingleton<JobDeduplicationGuardService>(sp =>
            new JobDeduplicationGuardService(sp.GetRequiredService<IAgentRegistryService>(), Log.Logger));

        // ── ITokenVendingService ─────────────────────────────────────────────
        services.AddHttpClient("TokenVending")
            .AddStandardResilienceHandler();
        services.AddSingleton<ITokenVendingService>(sp =>
            new TokenVendingService(Log.Logger, sp.GetRequiredService<IHttpClientFactory>()));

        // ── ILabelService ────────────────────────────────────────────────────
        services.AddSingleton<ILabelService>(sp => new LabelService(
            sp.GetRequiredService<IProviderConfigStore>(),
            sp.GetRequiredService<IProviderFactory>(),
            Log.Logger));

        // ── ILabelSwapService ─────────────────────────────────────────────────
        // Registered conditionally so the API degrades gracefully when ILabelService is unconfigured.
        // LabelSwapService is internal sealed — accessed here through the same assembly.
        services.AddSingleton<ILabelSwapService>(sp =>
            new LabelSwapService(
                sp.GetRequiredService<ILabelService>(),
                sp.GetRequiredService<ILoggerFactory>().CreateLogger<LabelSwapService>()));

        // ── PipelineRunLifecycleService — implements IChangeNotifier + IChatNotifier ──
        services.AddSingleton<PipelineRunLifecycleService>(sp => new PipelineRunLifecycleService(
            sp.GetRequiredService<IPipelineRunHistoryService>(),
            sp.GetRequiredService<IOrchestratorRunService>(),
            Log.Logger,
            sp.GetService<IAgentCancellationSender>()));
        services.AddSingleton<IChangeNotifier>(sp => sp.GetRequiredService<PipelineRunLifecycleService>());
        services.AddSingleton<IChatNotifier>(sp => sp.GetRequiredService<PipelineRunLifecycleService>());

        // ── IConsolidationService ────────────────────────────────────────────
        services.AddSingleton<IConsolidationService>(sp => new ConsolidationService(
            new ConsolidationServiceDependencies(
                Log.Logger,
                new PipelineConfiguration(),
                sp.GetRequiredService<IProjectStore>(),
                sp.GetRequiredService<IPipelineRunHistoryService>(),
                sp.GetRequiredService<IConsolidationRunStore>(),
                sp.GetRequiredService<IHarnessSuggestionStore>())));

        // ── ModelFetchService ────────────────────────────────────────────────
        services.AddSingleton<ModelFetchService>(sp => new ModelFetchService(
            sp.GetRequiredService<IAgentRegistryService>(),
            sp.GetRequiredService<IAgentCommunication>(),
            Log.Logger));

        // ── ConsolidationBadgeService ────────────────────────────────────────
        services.AddSingleton<ConsolidationBadgeService>();

        // ── IAgentCommunication → SignalRAgentCommunication ──────────────────
        services.AddSingleton<IAgentCommunication>(sp =>
            new SignalRAgentCommunication(
                sp.GetRequiredService<Microsoft.AspNetCore.SignalR.IHubContext<AgentHub, IAgentHubClient>>()));

        // ── IActiveRunQueryService ────────────────────────────────────────────
        services.AddSingleton<IActiveRunQueryService>(sp => new PostgresActiveRunQueryService(
            sp.GetRequiredService<IDbContextFactory<PipelineDbContext>>(),
            sp.GetRequiredService<IOrchestratorRunService>()));

        // ── IRunLifecycleManager ──────────────────────────────────────────────
        services.AddSingleton<IRunLifecycleManager>(sp => new RunLifecycleManager(
            new RunLifecycleManagerDependencies(
                sp.GetRequiredService<IOrchestratorRunService>(),
                sp.GetRequiredService<IPipelineRunHistoryService>(),
                sp.GetRequiredService<IAgentRegistryService>(),
                sp.GetRequiredService<ILabelService>(),
                sp.GetRequiredService<AgentReservationService>(),
                Log.Logger,
                sp.GetService<IJobCleanupStrategy>(),
                sp.GetRequiredService<IWorkItemFallbackTransitionService>())));

        // ── IKubernetes ──────────────────────────────────────────────────────────────────────
        // Required by IKubernetesJobClient (ModelFetchJobService, ChatJobDispatcher).
        services.AddSingleton<IKubernetes>(sp =>
        {
            try
            {
                var inCluster = KubernetesClientConfiguration.IsInCluster();
                var config = inCluster
                    ? KubernetesClientConfiguration.InClusterConfig()
                    : KubernetesClientConfiguration.BuildDefaultConfig();

                Log.Information("Kubernetes client configured (API): Source={Source} Host={Host}",
                    inCluster ? "in-cluster" : "kubeconfig", config.Host);

                if (string.IsNullOrEmpty(config.Host) || config.Host == "http://localhost:8080")
                {
                    Log.Warning("API: Kubernetes client host is empty or localhost — K8s unavailable.");
                    return null!;
                }

                return new k8s.Kubernetes(config);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "API: Kubernetes client unavailable — leader election inactive. " +
                                "DatabaseMaintenanceService sweep runs ungated.");
                return null!;
            }
        });
        // IKubernetesJobClient wraps IKubernetes. Use GetService (nullable) so the factory
        // returns a no-op stub when K8s is unavailable, instead of passing null to
        // KubernetesJobClient which would NRE on the first dispatch attempt.

        // ── IConsolidationJobPreparationService ────────────────────────────
        // Required by the ConsolidationWorkItemEndpoints (POST /api/consolidation-work-items/{id}/claim)
        // to resolve provider configs and vend short-lived tokens at claim time.
        // Also used by ReportConsolidationComplete hub handling.
        services.AddSingleton<IConsolidationJobPreparationService>(sp =>
            new ConsolidationJobPreparationService(
                sp.GetRequiredService<IProviderConfigStore>(),
                sp.GetRequiredService<IProjectStore>(),
                sp.GetRequiredService<ITokenVendingService>(),
                Log.Logger,
                sp.GetRequiredService<IAgentProfileStore>()));

        // ── IKubernetesJobClient ─────────────────────────────────────────────
        // Required by ModelFetchJobService and ChatJobDispatcher.
        // IKubernetes is already registered above; only the job client wrapper is missing.
        services.AddSingleton<IKubernetesJobClient>(sp =>
        {
            var k8s = sp.GetService<IKubernetes>();
            if (k8s is null)
            {
                Log.Warning("API: IKubernetesJobClient unavailable — K8s not configured. ModelFetch and ChatJobDispatcher will fail if triggered.");
                return null!;
            }
            return new KubernetesJobClient(k8s);
        });

        // ── IJobCleanupStrategy (1D-003) ─────────────────────────────────────
        // Registered here so RunLifecycleManager.CancelRunAsync (and FailRunAsync) can delete
        // the K8s Job on cancellation/failure, preventing the pod from consuming backoffLimit retries.
        // Gracefully degrades to no-op when IKubernetesJobClient is null (K8s unavailable).
        services.AddSingleton<IJobCleanupStrategy>(sp =>
        {
            var jobClient = sp.GetService<IKubernetesJobClient>();
            if (jobClient is null)
            {
                Log.Warning("API: IJobCleanupStrategy unavailable — IKubernetesJobClient not registered. K8s Jobs will not be deleted on cancel/fail.");
                return new NoOpJobCleanupStrategy();
            }
            var cfg = sp.GetRequiredService<IConfiguration>();
            var ns = cfg.GetValue<string>("WorkDistribution:Namespace") ?? "coding-agent";
            return new KubernetesJobCleanup(
                new DbWorkItemClientAdapter(sp.GetRequiredService<IDbContextFactory<PipelineDbContext>>()),
                jobClient,
                ns,
                Log.Logger);
        });

        // ── JobTemplateStore ─────────────────────────────────────────────────
        // Required by ModelFetchJobService and ChatJobDispatcher.
        // Path matches WorkDistribution__JobTemplatesPath env var set in api-deployment.yaml.
        services.AddSingleton(sp =>
        {
            var cfg = sp.GetRequiredService<IConfiguration>();
            var templatesPath = cfg.GetValue<string>("WorkDistribution:JobTemplatesPath")
                ?? "/app/config/job-templates.yaml";
            if (!System.IO.File.Exists(templatesPath))
            {
                Serilog.Log.Warning("Job templates file not found at {Path}; starting with empty template store", templatesPath);
                return JobTemplateStore.CreateEmpty();
            }
            return JobTemplateStore.LoadFromFile(templatesPath);
        });

        // ── DatabaseMaintenanceService ────────────────────────────────────────────────────────
        // The only retention sweep in the system — orphaning it causes Postgres to grow
        // without bound while retention settings still render in the UI.
        // Registered as a singleton (not hosted) so the maintenance endpoint in ApiSchedulerEndpoints
        // can resolve and invoke RunRetentionSweepAsync directly. The Scheduler triggers sweeps
        // via POST /api/scheduler/maintenance/retention-sweep.
        // Leader gating removed (Spec 049): the Scheduler's RetentionSweepSchedulerService
        // gates on its own leader election — no API-side lease needed.
        services.AddSingleton<DatabaseMaintenanceService>(sp => new DatabaseMaintenanceService(
            sp.GetRequiredService<IDbContextFactory<PipelineDbContext>>(),
            sp.GetRequiredService<IConsolidationService>(),
            sp.GetRequiredService<IConfiguration>(),
            sp.GetRequiredService<IPipelineConfigStore>()));

        // ── WorkItemMetricsBackgroundService ──────────────────────────────────────────────────
        // Spec 047: Removed from API hosted services — replaced by WorkItemCountsPoller in
        // CodingAgentWebUI.Scheduler. WorkItemCountsPoller polls GET /api/work-items/counts-by-status
        // and registers the same WorkDistributionTelemetry callback from the Scheduler process.

        // ── ModelFetchJobService ─────────────────────────────────────────────────────────────
        // Singleton in the API. The API has K8s RBAC for batch/jobs.
        services.AddSingleton<ModelFetchJobService>(sp => new ModelFetchJobService(
            new ModelFetchJobDependencies(
                sp.GetRequiredService<IKubernetesJobClient>(),
                sp.GetRequiredService<JobTemplateStore>(),
                DispatchServiceOptionsFactory.Create(sp.GetRequiredService<IConfiguration>()),
                sp.GetRequiredService<IPipelineConfigStore>(),
                sp.GetRequiredService<ModelFetchService>(),
                Logger: Log.Logger)));

        // ── ChatJobDispatcher — on-demand ephemeral chat pod dispatch ────────────────────────
        // Moved from the Blazor monolith to the API host (Spec 044/045 follow-up).
        // The monolith no longer maps AgentHub, so IHubContext<AgentHub> on the monolith was
        // disconnected from any real agents. The API host owns the hub and the AgentRegistryService,
        // making it the correct process for chat dispatch and the registry poll loop.
        // Spec 049: ILeaderElectionService removed — all replicas can dispatch. The K8s
        // double-dispatch guard (CheckForExistingJob) is already replica-safe.
        services.AddSingleton<ChatJobDispatcher>(sp =>
        {
            var options = DispatchServiceOptionsFactory.Create(sp.GetRequiredService<IConfiguration>());
            options.ValidateAndClamp(Log.Logger);
            var mux = sp.GetService<StackExchange.Redis.IConnectionMultiplexer>();
            CodingAgentWebUI.Orchestration.Redis.IRedisStore? redisStore = mux is not null
                ? new CodingAgentWebUI.Orchestration.Redis.RedisStore(mux.GetDatabase())
                : null;
            return new ChatJobDispatcher(
                sp.GetRequiredService<IKubernetesJobClient>(),
                sp.GetRequiredService<Microsoft.AspNetCore.SignalR.IHubContext<AgentHub, IAgentHubClient>>(),
                sp.GetRequiredService<JobTemplateStore>(),
                sp.GetRequiredService<IAgentRegistryService>(),
                options,
                Log.Logger,
                redisStore);
        });
        services.AddHostedService(sp => sp.GetRequiredService<ChatJobDispatcher>());
        services.AddSingleton<IChatJobDispatcher>(sp => sp.GetRequiredService<ChatJobDispatcher>());

        return services;
    }
}

/// <summary>
/// No-op database probe for integration test environments and the API service,
/// where the startup service manages DB connectivity independently.
/// </summary>
public sealed class NoOpDatabaseProbe : IDatabaseProbe
{
    public Task ProbeAsync(CancellationToken ct) => Task.CompletedTask;
}
