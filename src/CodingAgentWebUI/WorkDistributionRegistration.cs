using CodingAgentWebUI.Infrastructure;
using CodingAgentWebUI.Infrastructure.Locking;
using CodingAgentWebUI.Infrastructure.Persistence;
using CodingAgentWebUI.Infrastructure.Persistence.Services;
using CodingAgentWebUI.Infrastructure.Persistence.Stores;
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
/// Registers work distribution services based on deployment mode:
/// - No Database:Host → Legacy mode (JSON + in-memory)
/// - DB + SignalR mode → PostgresConfigurationStore + SignalRWorkDistributor
/// - DB + Kubernetes mode → full K8s services with DispatchService + ReconciliationService
/// </summary>
public static partial class WorkDistributionRegistration
{
    /// <summary>
    /// Configures work distribution mode and registers all mode-dependent services.
    /// Must be called AFTER AddInfrastructureServices (legacy) or INSTEAD of it (DB modes).
    /// </summary>
    public static IServiceCollection AddWorkDistribution(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var connectionString = Services.DatabaseConnectionResolver.Resolve(configuration);
        var mode = configuration.GetValue<string>("WorkDistribution:Mode") ?? "SignalR";

        if (string.IsNullOrEmpty(connectionString))
        {
            RegisterLegacyMode(services);
            return services;
        }

        // ── DB mode: validate mode value ────────────────────────────────────
        if (!string.Equals(mode, "SignalR", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(mode, "Kubernetes", StringComparison.OrdinalIgnoreCase))
        {
            Log.Error("Unrecognized WorkDistribution:Mode '{Mode}'. Valid values: 'SignalR', 'Kubernetes'", mode);
            throw new InvalidOperationException(
                $"Unrecognized WorkDistribution:Mode '{mode}'. Valid values: 'SignalR', 'Kubernetes'.");
        }

        var isKubernetesMode = string.Equals(mode, "Kubernetes", StringComparison.OrdinalIgnoreCase);

        // ── K8s mode: fail if not in cluster ────────────────────────────────
        if (isKubernetesMode && !IsRunningInKubernetesCluster())
        {
            Log.Error("WorkDistribution:Mode is 'Kubernetes' but the application is not running inside a Kubernetes cluster");
            throw new InvalidOperationException(
                "WorkDistribution:Mode is 'Kubernetes' but the application is not running inside a Kubernetes cluster. " +
                "The service account token path '/var/run/secrets/kubernetes.io/serviceaccount/token' was not found.");
        }

        // ── Normalize connection string (Timeout=15, SslMode=Require for production) ──
        var isProduction = !string.Equals(
            Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT"),
            "Development",
            StringComparison.OrdinalIgnoreCase);
        var normalizedConnectionString = Services.DatabaseReadinessMonitor.NormalizeConnectionString(
            connectionString, isProduction);

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

        // ── DispatchOrchestrationService (DB modes only — null in Legacy mode) ──
        services.AddSingleton<IDispatchOrchestrationService>(sp => new DispatchOrchestrationService(
            sp.GetRequiredService<DispatchInfrastructure>(),
            sp.GetRequiredService<Pipeline.Interfaces.IDispatchRunCreator>(),
            sp.GetRequiredService<IOrchestratorRunService>(),
            sp.GetRequiredService<Pipeline.Interfaces.IWorkDistributor>(),
            sp.GetRequiredService<Pipeline.Interfaces.IAgentProfileStore>(),
            sp.GetRequiredService<Pipeline.Interfaces.IConfigurationStore>(),
            sp.GetRequiredService<Pipeline.Interfaces.IPipelineConfigStore>(),
            sp.GetRequiredService<Pipeline.Interfaces.IProjectStore>(),
            Log.Logger,
            sp.GetRequiredService<Pipeline.Interfaces.IWorkItemQueryService>()));

        // ── IRunLifecycleManager (DB mode — coordinates in-memory + DB transitions) ──
        // TODO: Use GetRequiredService<IJobCleanupStrategy>() instead of GetService to fail fast on
        // misconfiguration (both K8s and SignalR modes always register an implementation).
        services.AddSingleton<IRunLifecycleManager>(sp => new Orchestration.RunLifecycleManager(
            sp.GetRequiredService<IOrchestratorRunService>(),
            sp.GetRequiredService<IPipelineRunHistoryService>(),
            sp.GetRequiredService<AgentRegistryService>(),
            sp.GetRequiredService<ILabelService>(),
            sp.GetRequiredService<JobDeduplicationGuardService>(),
            Log.Logger,
            sp.GetRequiredService<WorkItemTransitionService>(),
            sp.GetService<IJobCleanupStrategy>()));

        // ── PostgresConfigurationStore (replaces JsonConfigurationStore) ─────
        // Singleton: consumed by singleton services (LabelService, DispatchResolutionService,
        // HeartbeatMonitorService, AgentHubFacade). Uses IDbContextFactory internally
        // (creates/disposes contexts per operation), so singleton lifetime is correct.
        // Internal MemoryCache + _pipelineConfigCache only work correctly as singleton.
        services.AddSingleton<IConfigurationStore>(sp =>
            new PostgresConfigurationStore(sp.GetRequiredService<IDbContextFactory<PipelineDbContext>>()));
        RegisterConfigStoreSubInterfaces(services);

        // ── Consolidation run persistence (DB-backed) ───────────────────────
        services.AddSingleton<IConsolidationRunStore>(sp =>
            new PostgresConsolidationRunStore(sp.GetRequiredService<IDbContextFactory<PipelineDbContext>>()));

        // ── Loop state persistence (DB-backed) ──────────────────────────────
        services.AddSingleton<ILoopStateStore>(sp =>
            new PostgresLoopStateStore(sp.GetRequiredService<IDbContextFactory<PipelineDbContext>>()));

        // ── Harness suggestions persistence (DB-backed) ─────────────────────
        services.AddSingleton<IHarnessSuggestionStore>(sp =>
            new PostgresHarnessSuggestionStore(sp.GetRequiredService<IDbContextFactory<PipelineDbContext>>()));

        // ── Polly resilience pipelines ──────────────────────────────────────
        RegisterResiliencePipelines(services);

        // ── WorkItem metrics background service (DB-mode only) ──────────────
        services.AddHostedService<WorkItemMetricsBackgroundService>();

        // ── Mode-specific registrations ─────────────────────────────────────
        if (isKubernetesMode)
        {
            RegisterKubernetesMode(services, configuration);
        }
        else
        {
            RegisterSignalRMode(services, configuration);
        }

        // ── SignalR Redis backplane (optional, both DB modes) ────────────────
        ConfigureSignalRRedisBackplane(services, configuration);

        Log.Information("WorkDistribution: {Mode} mode with PostgreSQL. ConnectionString configured",
            isKubernetesMode ? "Kubernetes" : "SignalR");

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
        var connectionString = Services.DatabaseConnectionResolver.Resolve(configuration);
        if (string.IsNullOrEmpty(connectionString))
            return services; // No DB — no additional instrumentation needed

        // OTel instrumentation is added to the existing OpenTelemetry builder in Program.cs
        // via the tracing/metrics builder callbacks. This method is a marker for the
        // configuration to be applied in the WithTracing/WithMetrics calls.
        return services;
    }

    /// <summary>
    /// Registers all IConfigurationStore sub-interface forwarding registrations.
    /// Ensures both JSON and Postgres paths register the same set of sub-interfaces.
    /// MUST be called AFTER IConfigurationStore itself is registered.
    /// </summary>
    internal static void RegisterConfigStoreSubInterfaces(IServiceCollection services)
    {
        services.AddSingleton<IPipelineConfigStore>(sp => sp.GetRequiredService<IConfigurationStore>());
        services.AddSingleton<IProviderConfigStore>(sp => sp.GetRequiredService<IConfigurationStore>());
        services.AddSingleton<IAgentProfileStore>(sp => sp.GetRequiredService<IConfigurationStore>());
        services.AddSingleton<IQualityGateConfigStore>(sp => sp.GetRequiredService<IConfigurationStore>());
        services.AddSingleton<IReviewerConfigStore>(sp => sp.GetRequiredService<IConfigurationStore>());
        services.AddSingleton<IProjectStore>(sp => sp.GetRequiredService<IConfigurationStore>());
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

    /// <summary>
    /// Detects whether the process is running inside a Kubernetes cluster
    /// by checking for the service account token file.
    /// </summary>
    private static bool IsRunningInKubernetesCluster()
    {
        // Standard K8s service account token mount path
        const string tokenPath = "/var/run/secrets/kubernetes.io/serviceaccount/token";
        if (File.Exists(tokenPath))
            return true;

        // Fallback: KUBERNETES_SERVICE_HOST env var is always set in-cluster
        var k8sServiceHost = Environment.GetEnvironmentVariable("KUBERNETES_SERVICE_HOST");
        return !string.IsNullOrEmpty(k8sServiceHost);
    }

    /// <summary>
    /// Determines if an exception is a transient database error eligible for retry.
    /// </summary>
    private static bool IsTransientDbException(Exception ex)
    {
        // Npgsql transient errors
        if (ex is Npgsql.NpgsqlException npgsqlEx && npgsqlEx.IsTransient)
            return true;

        // EF Core concurrency conflicts are not transient in the retry sense
        if (ex is Microsoft.EntityFrameworkCore.DbUpdateConcurrencyException)
            return false;

        // Generic timeout or I/O errors
        if (ex is TimeoutException or System.IO.IOException)
            return true;

        return false;
    }
}
