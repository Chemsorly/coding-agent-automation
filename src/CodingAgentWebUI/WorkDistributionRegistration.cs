using CodingAgentWebUI.Infrastructure;
using CodingAgentWebUI.Infrastructure.Persistence;
using CodingAgentWebUI.Infrastructure.Persistence.Services;
using CodingAgentWebUI.Infrastructure.Persistence.Stores;
using CodingAgentWebUI.Infrastructure.Locking;
using CodingAgentWebUI.Kubernetes;
using CodingAgentWebUI.Orchestration.Dispatch;
using CodingAgentWebUI.Pipeline.Interfaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Polly.Registry;
using Serilog;
using StackExchange.Redis;

namespace CodingAgentWebUI;

/// <summary>
/// Registers work distribution services for Kubernetes deployment.
/// Spec 045 Req 1.2/1.3: High-level DB-backed config/history stores removed.
/// Remaining DB registrations (IDbContextFactory, WorkItemTransitionService,
/// IPipelineRunHistoryService, IConsolidationRunStore, IHarnessSuggestionStore):
/// still required by KubernetesWorkDistributor, KubernetesJobCleanup, consolidation
/// services, and PipelineRunLifecycleService. A follow-up spec migrates those to
/// API calls and removes the last IDbContextFactory usage from the monolith.
/// </summary>
public static partial class WorkDistributionRegistration
{
    /// <summary>
    /// Registers work distribution services: K8s infrastructure, leader election,
    /// work distributor, and SignalR backplane.
    /// All high-level Postgres-backed config/key-value stores removed (Spec 045 Req 1.2).
    /// </summary>
    public static IServiceCollection AddWorkDistribution(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var connectionString = DatabaseConnectionResolver.Resolve(configuration);
        if (string.IsNullOrEmpty(connectionString))
        {
            Log.Fatal("Database__Host is not configured. Kubernetes deployment requires PostgreSQL.");
            throw new InvalidOperationException("Database__Host is not configured.");
        }

        // ── Normalize connection string (Timeout=15, SslMode=Require for production) ──
        var isProduction = !string.Equals(
            Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT"),
            "Development",
            StringComparison.OrdinalIgnoreCase);
        var normalizedConnectionString = Services.DatabaseReadinessMonitor.NormalizeConnectionString(
            connectionString, isProduction);

        // ── EF Core DbContext Factory + scoped accessor ─────────────────────
        // Still required by: KubernetesWorkDistributor (DbWorkDistributorBase), KubernetesJobCleanup,
        // IPipelineRunHistoryService (PostgresPipelineRunHistoryService),
        // IConsolidationRunStore (PostgresConsolidationRunStore),
        // IHarnessSuggestionStore (PostgresHarnessSuggestionStore).
        // TODO(Spec 046): migrate those to API calls to complete DB removal.
        services.AddPooledDbContextFactory<PipelineDbContext>(opts =>
            opts.UseNpgsql(normalizedConnectionString));
        services.AddScoped(sp =>
            sp.GetRequiredService<IDbContextFactory<PipelineDbContext>>().CreateDbContext());

        // ── Distributed lock provider (Postgres advisory locks) ─────────────
        services.AddDistributedLockProvider(connectionString);

        // ── WorkItemTransitionService — still needed by KubernetesWorkDistributor ────
        services.AddSingleton<WorkItemTransitionService>(sp => new WorkItemTransitionService(
            sp.GetRequiredService<IDbContextFactory<PipelineDbContext>>(),
            sp.GetRequiredService<ILoggerFactory>().CreateLogger<WorkItemTransitionService>(),
            sp.GetService<ResiliencePipelineProvider<string>>()));

        // ── WorkItemFallbackTransitionService (singleton — wraps WorkItemTransitionService) ──
        services.AddSingleton<IWorkItemFallbackTransitionService>(sp => new WorkItemFallbackTransitionService(
            sp.GetRequiredService<WorkItemTransitionService>(),
            sp.GetRequiredService<ILoggerFactory>().CreateLogger<WorkItemFallbackTransitionService>()));

        // ── IPipelineRunHistoryService — still needed by consolidation services and PipelineRunLifecycleService ──
        // Spec 045 Req 1.2: PostgresPipelineRunHistoryService is the only remaining DB-backed service
        // in this registration that will stay until consolidation services are migrated to the API.
        services.AddSingleton<IPipelineRunHistoryService>(sp => new PostgresPipelineRunHistoryService(
            sp.GetRequiredService<IDbContextFactory<PipelineDbContext>>(),
            Log.Logger));

        // ── Consolidation run persistence (DB-backed) — still needed by consolidation services ────
        services.AddSingleton<IConsolidationRunStore>(sp =>
            new PostgresConsolidationRunStore(sp.GetRequiredService<IDbContextFactory<PipelineDbContext>>()));

        // ── Harness suggestions persistence (DB-backed) — still needed by ConsolidationService ────
        services.AddSingleton<IHarnessSuggestionStore>(sp =>
            new PostgresHarnessSuggestionStore(sp.GetRequiredService<IDbContextFactory<PipelineDbContext>>()));

        // REMOVED (Spec 045 Req 1.2):
        //   IActiveRunQueryService (PostgresActiveRunQueryService) — active runs now via IPipelineApiRunHistoryClient
        //   IWorkItemQueryService (WorkItemTransitionService) — staleness detector removed
        //   AnalysisStalenessDetector — registration removed; ServiceCollectionExtensions.JobDispatching
        //     uses GetService<AnalysisStalenessDetector>() (null-safe), so null is acceptable
        //   IDispatchOrchestrationService — Task 7 moved run creation to API; remaining Dispatch call
        //     goes through IWorkDistributor. TODO: remove from consolidation if no longer needed.
        //   PostgresConfigurationStore + RegisterConfigStoreSubInterfaces — replaced by API-backed adapters
        //     in ServiceCollectionExtensions.PipelineBackgroundServices (Task 6)
        //   ILoopStateStore (PostgresLoopStateStore) — Option B: ClosedLoopAutoStart in PipelineConfiguration
        //   IKeyValueStore (EfKeyValueStore) — replaced by IPipelineApiConfigClient (Task 3)
        //   WorkItemMetricsBackgroundService — re-homed to CodingAgentWebUI.Api (Task 8a)
        //   DatabaseMaintenanceService — re-homed to CodingAgentWebUI.Api (Task 8a)

        // ── Polly resilience pipelines (no DB dependency) ────────────────────
        services.RegisterResiliencePipelines();

        // ── K8s infrastructure + consolidation registrations ─────────────────────────────────────
        RegisterConsolidationServices(services, configuration);

        // ── SignalR Redis backplane (optional) ────────────────────────────────
        ConfigureSignalRRedisBackplane(services, configuration);

        Log.Information("WorkDistribution: Kubernetes mode — high-level config stores removed (API-backed); " +
                        "IDbContextFactory retained for KubernetesWorkDistributor and consolidation services");

        return services;
    }

    /// <summary>
    /// Configures OpenTelemetry tracing and metrics for work distribution dependencies.
    /// Call after AddOpenTelemetry() in the pipeline.
    /// </summary>
    public static IServiceCollection AddWorkDistributionTelemetry(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        // Marker method — PostgreSQL is always required (Program.cs fast-fail).
        // OTel instrumentation is added to the existing OpenTelemetry builder in Program.cs
        // via the tracing/metrics builder callbacks. This method is a hook for any future
        // work-distribution-specific instrumentation setup.
        return services;
    }

    /// <summary>
    /// Wires SignalR Redis backplane when SignalR:Redis:ConnectionString is configured.
    /// Without Redis, uses default in-memory transport (single replica / docker-compose).
    /// Called for both DB modes (SignalR and Kubernetes) since the Redis backplane is for
    /// the SignalR hub used by the web UI, not the work distribution mode.
    /// </summary>
    private static void ConfigureSignalRRedisBackplane(IServiceCollection services, IConfiguration configuration)
    {
        var redisConnectionString = configuration.GetValue<string>("SignalR:Redis:ConnectionString");
        if (string.IsNullOrEmpty(redisConnectionString))
            return;

        // Shared config and connection reference — used by both the SignalR factory and DI registration.
        var config = ConfigurationOptions.Parse(redisConnectionString);
        config.ChannelPrefix = RedisChannel.Literal("caa");
        config.AbortOnConnectFail = false;
        config.ConnectRetry = 5;
        config.ReconnectRetryPolicy = new ExponentialRetry(5000, 55000);

        IConnectionMultiplexer? sharedConnection = null;
        var connectionLock = new object();

        // Replace the default AddSignalR() registration with Redis backplane.
        // Note: AddSignalR() is already called in Program.cs. AddStackExchangeRedis extends it.
        services.AddSignalR().AddStackExchangeRedis(options =>
        {
            options.Configuration = config;

            options.ConnectionFactory = async (writer) =>
            {
                // With AbortOnConnectFail=false, ConnectAsync returns immediately with a
                // disconnected multiplexer that retries in the background. This ensures
                // startup never crashes due to Redis unavailability.
                var connection = await ConnectionMultiplexer.ConnectAsync(config, writer);
                connection.ConnectionFailed += (_, e) =>
                    Log.Warning("Redis backplane connection failed: {FailureType} — {Exception}",
                        e.FailureType, e.Exception?.Message);
                connection.ConnectionRestored += (_, e) =>
                    Log.Information("Redis backplane connection restored: {EndPoint}", e.EndPoint);

                // Capture the connection for DI health checks (single assignment, no awaits in lock)
                lock (connectionLock) { sharedConnection = connection; }

                return connection;
            };
        });

        // Register IConnectionMultiplexer as a lazy singleton so InfrastructureHealthService
        // can resolve it for health checks. The factory delegate above sets sharedConnection
        // when SignalR first creates the Redis connection. Fallback uses the same resilient config
        // (AbortOnConnectFail=false) to avoid throwing on transient Redis unavailability.
        services.AddSingleton<IConnectionMultiplexer>(sp =>
        {
            lock (connectionLock)
            {
                return sharedConnection
                    ?? ConnectionMultiplexer.Connect(config);
            }
        });

        Log.Information("WorkDistribution: SignalR Redis backplane configured with AbortOnConnectFail=false");
    }
}
