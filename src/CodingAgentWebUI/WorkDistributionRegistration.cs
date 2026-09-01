using CodingAgentWebUI.Api.Client;
using CodingAgentWebUI.Infrastructure;
using CodingAgentWebUI.Infrastructure.Persistence.Services;
using CodingAgentWebUI.Infrastructure.Persistence.Stores;
using CodingAgentWebUI.Kubernetes;
using CodingAgentWebUI.Orchestration.Dispatch;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Services;
using Serilog;
using StackExchange.Redis;

namespace CodingAgentWebUI;

/// <summary>
/// Registers work distribution services for Kubernetes deployment.
/// All persistence routes through the Pipeline API — no direct Postgres access in the orchestrator.
/// T8 complete (arch-audit 2026-08-22): IDbContextFactory removed from orchestrator registration.
/// </summary>
public static partial class WorkDistributionRegistration
{
    /// <summary>
    /// Registers work distribution services: K8s infrastructure, leader election,
    /// work distributor, and SignalR backplane.
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
            new CodingAgentWebUI.Api.Client.Stores.ApiBackedPipelineRunHistoryService(
                sp.GetRequiredService<IPipelineApiRunHistoryClient>(),
                Log.Logger));

        // ── IWorkItemFallbackTransitionService — API-backed (T8 item 3) ─────
        // WorkItem status transitions route through POST /api/work-items/{id}/status.
        // IWorkItemFallbackTransitionService is no longer backed by direct EF Core access.
        services.AddSingleton<IWorkItemFallbackTransitionService>(sp =>
            new CodingAgentWebUI.Services.ApiBackedWorkItemFallbackTransitionService(
                sp.GetRequiredService<IPipelineApiWorkItemClient>(),
                Log.Logger));

        // ── Consolidation run persistence — API-backed ──────────────────────────────────────────
        // The API is the sole owner of the ConsolidationRuns table. The orchestrator must not
        // write to it directly — that caused dual-write race conditions where the API's status
        // updates were overwritten by the orchestrator's CleanupOrphanedRunsAsync on restart.
        services.AddSingleton<IConsolidationRunStore>(sp =>
            new ApiBackedConsolidationRunStore(sp.GetRequiredService<IPipelineApiConsolidationRunClient>()));

        // ── Harness suggestions persistence — API-backed ────────────────────────────────────────
        services.AddSingleton<IHarnessSuggestionStore>(sp =>
            new ApiBackedHarnessSuggestionStore(sp.GetRequiredService<IPipelineApiHarnessSuggestionClient>()));

        // The following services were removed — registrations live in CodingAgentWebUI.Api:
        //   IActiveRunQueryService → IPipelineApiRunHistoryClient
        //   AnalysisStalenessDetector → deleted (B6, arch-audit 2026-08-22); was never registered
        //   PostgresConfigurationStore → API-backed adapters in ServiceCollectionExtensions.PipelineBackgroundServices
        //   ILoopStateStore → ClosedLoopAutoStart in PipelineConfiguration
        //   IKeyValueStore → IPipelineApiConfigClient
        //   WorkItemMetricsBackgroundService → CodingAgentWebUI.Api
        //   DatabaseMaintenanceService → CodingAgentWebUI.Api

        // ── Polly resilience pipelines (no DB dependency) ────────────────────
        services.RegisterResiliencePipelines();

        // ── K8s infrastructure + consolidation registrations ─────────────────────────────────────
        RegisterConsolidationServices(services, configuration);

        // ── SignalR Redis backplane (optional) ────────────────────────────────
        ConfigureSignalRRedisBackplane(services, configuration);

        Log.Information("WorkDistribution: Kubernetes mode — all persistence routes through Pipeline API. " +
                        "No direct Postgres access in orchestrator. T8 complete.");

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
    /// <summary>
    /// Wires SignalR Redis backplane when SignalR:Redis:ConnectionString is configured.
    /// Without Redis, uses default in-memory transport (single replica only).
    /// The Redis backplane is for the SignalR hub used by the web UI, not for work distribution.
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
