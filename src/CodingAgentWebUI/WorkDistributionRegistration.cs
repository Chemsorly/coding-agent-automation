using CodingAgentWebUI.Infrastructure;
using CodingAgentWebUI.Infrastructure.Locking;
using CodingAgentWebUI.Infrastructure.Persistence;
using CodingAgentWebUI.Infrastructure.Persistence.Services;
using CodingAgentWebUI.Infrastructure.Persistence.Stores;
using CodingAgentWebUI.Kubernetes;
using CodingAgentWebUI.Orchestration;
using CodingAgentWebUI.Orchestration.Dispatch;
using CodingAgentWebUI.Orchestration.Registry;
using CodingAgentWebUI.Orchestration.Telemetry;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Services;
using CodingAgentWebUI.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Polly.Registry;
using Serilog;
using StackExchange.Redis;

namespace CodingAgentWebUI;

/// <summary>
/// Registers work distribution services for Kubernetes deployment.
/// Kubernetes is the only supported work distribution mode — PostgreSQL is required.
/// </summary>
public static partial class WorkDistributionRegistration
{
    /// <summary>
    /// Registers all work distribution services using Kubernetes mode.
    /// Must be called after the Database__Host fast-fail in Program.cs guarantees
    /// a non-empty connection string.
    /// </summary>
    public static IServiceCollection AddWorkDistribution(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var connectionString = DatabaseConnectionResolver.Resolve(configuration);

        // ── Normalize connection string (Timeout=15, SslMode=Require for production) ──
        var isProduction = !string.Equals(
            Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT"),
            "Development",
            StringComparison.OrdinalIgnoreCase);
        // connectionString is guaranteed non-null/non-empty by the Program.cs fast-fail
        // (Log.Fatal + return when Database__Host is not configured).
        var normalizedConnectionString = Services.DatabaseReadinessMonitor.NormalizeConnectionString(
            connectionString!, isProduction);

        // ── EF Core DbContext Factory + scoped accessor ─────────────────────
        services.AddPooledDbContextFactory<PipelineDbContext>(opts =>
            opts.UseNpgsql(normalizedConnectionString));
        services.AddScoped(sp =>
            sp.GetRequiredService<IDbContextFactory<PipelineDbContext>>().CreateDbContext());

        // ── Distributed lock provider (Postgres advisory locks) ─────────────
        services.AddDistributedLockProvider(connectionString);

        // ── WorkItemTransitionService (singleton, uses factory + Polly pipeline) ──────────────
        services.AddSingleton<WorkItemTransitionService>(sp => new WorkItemTransitionService(
            sp.GetRequiredService<IDbContextFactory<PipelineDbContext>>(),
            sp.GetRequiredService<ILoggerFactory>().CreateLogger<WorkItemTransitionService>(),
            sp.GetService<ResiliencePipelineProvider<string>>()));

        // ── WorkItemFallbackTransitionService (singleton — wraps WorkItemTransitionService) ──
        services.AddSingleton<IWorkItemFallbackTransitionService>(sp => new WorkItemFallbackTransitionService(
            sp.GetRequiredService<WorkItemTransitionService>(),
            sp.GetRequiredService<ILoggerFactory>().CreateLogger<WorkItemFallbackTransitionService>()));

        // ── IActiveRunQueryService (DB mode — queries Postgres for active run state) ──
        services.AddSingleton<IActiveRunQueryService>(sp => new PostgresActiveRunQueryService(
            sp.GetRequiredService<IDbContextFactory<PipelineDbContext>>(),
            sp.GetRequiredService<IOrchestratorRunService>()));

        // ── IPipelineRunHistoryService (DB mode — persists to PipelineRuns table) ──
        services.AddSingleton<IPipelineRunHistoryService>(sp => new PostgresPipelineRunHistoryService(
            sp.GetRequiredService<IDbContextFactory<PipelineDbContext>>(),
            Log.Logger));

        // ── IWorkItemQueryService (staleness detection queries) ──
        services.AddSingleton<Pipeline.Interfaces.IWorkItemQueryService>(sp =>
            sp.GetRequiredService<WorkItemTransitionService>());

        // ── AnalysisStalenessDetector (DB mode — evaluates analysis freshness signals) ──
        services.AddSingleton<Orchestration.Dispatch.AnalysisStalenessDetector>(sp =>
            new Orchestration.Dispatch.AnalysisStalenessDetector(
                sp.GetRequiredService<Pipeline.Interfaces.IWorkItemQueryService>(), Log.Logger));

        // ── DispatchOrchestrationService (DB modes only — null in Legacy mode) ──
        services.AddSingleton<IDispatchOrchestrationService>(sp =>
        {
            var infra = sp.GetRequiredService<DispatchInfrastructure>();

            return new DispatchOrchestrationService(
                new Orchestration.Dispatch.DispatchOrchestrationServiceDependencies(
                    infra,
                    sp.GetRequiredService<Pipeline.Interfaces.IDispatchRunCreator>(),
                    sp.GetRequiredService<IOrchestratorRunService>(),
                    sp.GetRequiredService<Pipeline.Interfaces.IWorkDistributor>(),
                    sp.GetRequiredService<Pipeline.Interfaces.IAgentProfileStore>(),
                    sp.GetRequiredService<Pipeline.Interfaces.IConfigurationStore>(),
                    sp.GetRequiredService<Pipeline.Interfaces.IPipelineConfigStore>()),
                Log.Logger);
        });

        // ── IRunLifecycleManager (DB mode — coordinates in-memory + DB transitions) ──
        // TODO: Use GetRequiredService<IJobCleanupStrategy>() instead of GetService to fail fast on
        // misconfiguration (both K8s and SignalR modes always register an implementation).
        services.AddSingleton<IRunLifecycleManager>(sp => new Orchestration.RunLifecycleManager(
            new Orchestration.RunLifecycleManagerDependencies(
                sp.GetRequiredService<IOrchestratorRunService>(),
                sp.GetRequiredService<IPipelineRunHistoryService>(),
                sp.GetRequiredService<AgentRegistryService>(),
                sp.GetRequiredService<ILabelService>(),
                sp.GetRequiredService<JobDeduplicationGuardService>(),
                Log.Logger,
                sp.GetRequiredService<WorkItemTransitionService>(),
                sp.GetService<IJobCleanupStrategy>(),
                sp.GetRequiredService<IWorkItemFallbackTransitionService>())));

        // ── PostgresConfigurationStore (replaces JsonConfigurationStore) ─────
        // Singleton: consumed by singleton services (LabelService, DispatchResolutionService,
        // HeartbeatMonitorService, AgentHubFacade). Uses IDbContextFactory internally
        // (creates/disposes contexts per operation), so singleton lifetime is correct.
        // Internal MemoryCache + _pipelineConfigCache only work correctly as singleton.
        services.AddSingleton<IConfigurationStore>(sp =>
            new PostgresConfigurationStore(sp.GetRequiredService<IDbContextFactory<PipelineDbContext>>()));
        services.RegisterConfigStoreSubInterfaces();

        // ── Consolidation run persistence (DB-backed) ───────────────────────
        services.AddSingleton<IConsolidationRunStore>(sp =>
            new PostgresConsolidationRunStore(sp.GetRequiredService<IDbContextFactory<PipelineDbContext>>()));

        // ── Loop state persistence (DB-backed) ──────────────────────────────
        services.AddSingleton<ILoopStateStore>(sp =>
            new PostgresLoopStateStore(sp.GetRequiredService<IDbContextFactory<PipelineDbContext>>()));

        // ── Generic key/value persistence (DB-backed) ────────────────────────
        services.AddScoped<IKeyValueStore, EfKeyValueStore>();

        // ── Harness suggestions persistence (DB-backed) ─────────────────────
        services.AddSingleton<IHarnessSuggestionStore>(sp =>
            new PostgresHarnessSuggestionStore(sp.GetRequiredService<IDbContextFactory<PipelineDbContext>>()));

        // ── Polly resilience pipelines ──────────────────────────────────────
        services.RegisterResiliencePipelines();

        // ── WorkItem metrics background service (DB-mode only) ──────────────
        services.AddHostedService<WorkItemMetricsBackgroundService>();

        // ── Database maintenance (retention cleanup — both DB modes) ────────
        services.AddHostedService<DatabaseMaintenanceService>();

        // ── Consolidation/surviving registrations ────────────────────────────────────────
        // IPendingWorkQuery, ChatJobDispatcher remain registered in the monolith.
        RegisterConsolidationServices(services, configuration);

        // ── SignalR Redis backplane (optional) ────────────────────────────────
        ConfigureSignalRRedisBackplane(services, configuration);

        Log.Information("WorkDistribution: Kubernetes mode with PostgreSQL. ConnectionString configured");

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
