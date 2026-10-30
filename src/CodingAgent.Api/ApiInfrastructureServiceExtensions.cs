using CodingAgent.AgentGateway;
using CodingAgent.Infrastructure;
using CodingAgent.Infrastructure.Common;
using CodingAgent.Infrastructure.Locking;
using CodingAgent.Infrastructure.Persistence;
using CodingAgent.Infrastructure.Persistence.Services;
using CodingAgent.Infrastructure.Persistence.Stores;
using CodingAgent.Orchestration;
using CodingAgent.Pipeline.Interfaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;
using Polly.Registry;
using Serilog;

namespace CodingAgent.Api;

/// <summary>
/// DI extension methods for API infrastructure services.
/// </summary>
public static class ApiInfrastructureServiceExtensions
{
    /// <summary>
    /// Registers infrastructure services for the Pipeline API:
    /// EF pooled factory, distributed lock, resilience pipelines, config store,
    /// run history, consolidation/loop-state stores, key-value store.
    /// </summary>
    public static IServiceCollection AddApiInfrastructure(
        this IServiceCollection services,
        string connectionString)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrEmpty(connectionString);

        // Normalize connection string (Timeout=15, SslMode=Require for production)
        var isProduction = !string.Equals(
            Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT"),
            "Development",
            StringComparison.OrdinalIgnoreCase);
        var normalizedConnectionString = DatabaseReadinessMonitor.NormalizeConnectionString(connectionString, isProduction);

        // ── EF Core DbContext Factory + scoped accessor ─────────────────────
        services.AddPooledDbContextFactory<PipelineDbContext>(opts =>
            opts.UseNpgsql(normalizedConnectionString, npgsqlOpts =>
                npgsqlOpts.EnableRetryOnFailure(
                    maxRetryCount: 3,
                    maxRetryDelay: TimeSpan.FromSeconds(5),
                    errorCodesToAdd: null)));
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
            sp.GetRequiredService<ILoggerFactory>().CreateLogger<WorkItemFallbackTransitionService>(),
            sp.GetRequiredService<IDbContextFactory<PipelineDbContext>>()));

        // ── IWorkItemTransitionStore (Spec 048 Phase 2 — DB isolation) ──────
        // Postgres-backed WorkItem operations (retry-count, re-queue, provider-config / issue-metadata
        // reads, throttled LastProgressAt write) for the SignalR agent hub facade. Wired only here
        // in the API host (the sole DB owner) so CodingAgent.AgentGateway carries no
        // Infrastructure.Persistence reference; the facade degrades to no-ops where it is absent.
        services.AddSingleton<IWorkItemTransitionStore>(sp => new PostgresWorkItemTransitionStore(
            sp.GetRequiredService<IDbContextFactory<PipelineDbContext>>(),
            sp.GetRequiredService<WorkItemTransitionService>()));

        // ── PostgresConfigurationStore with cache DISABLED (Req 5.6b) ──────
        // Cache is disabled via a negative TTL sentinel so two API replicas don't serve stale config.
        // With caching disabled, LoadPipelineConfigAsync always reads from the database, and
        // SavePipelineConfigAsync / UpdatePipelineConfigAsync do not populate the in-memory cache.
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

        // ── ILoopStateStore ─────────────────────────────────────────────────
        services.AddSingleton<ILoopStateStore>(sp =>
            new PostgresLoopStateStore(sp.GetRequiredService<IDbContextFactory<PipelineDbContext>>()));

        // ── IKeyValueStore ──────────────────────────────────────────────────
        services.AddScoped<IKeyValueStore, PostgresKeyValueStore>();

        // ── IFeedbackCommentOutbox ──────────────────────────────────────────
        // Registered as singleton (not scoped) because AgentJobLifecycleService is a singleton
        // and resolving a scoped service from the root container would throw at startup.
        // PostgresFeedbackCommentOutboxStore is safe as a singleton: it takes only
        // IDbContextFactory<PipelineDbContext> and uses a context-per-operation pattern.
        services.AddSingleton<IFeedbackCommentOutbox, PostgresFeedbackCommentOutboxStore>();

        // ── Triage ──────────────────────────────────────────────────────────
        // Singleton for the same reason: it takes only the DbContext factory. The hub's triage operations
        // record the results agents report.
        services.AddSingleton<ITriageStore>(sp =>
            new PostgresTriageStore(sp.GetRequiredService<IDbContextFactory<PipelineDbContext>>()));
        services.AddSingleton<IHubTriageOperations>(sp => new HubTriageOperations(
            sp.GetRequiredService<IAgentHubFacade>(),
            sp.GetRequiredService<ITriageStore>(),
            Serilog.Log.Logger));
        services.AddSingleton(sp => new CodingAgent.Api.Triage.TriageTrackerOperations(
            sp.GetRequiredService<IConfigurationStore>(),
            sp.GetRequiredService<IAgentProfileStore>(),
            sp.GetRequiredService<IProviderFactory>(),
            sp.GetRequiredService<ILabelService>(),
            Serilog.Log.Logger));
        services.AddSingleton(sp => new CodingAgent.Api.Triage.TriageService(
            sp.GetRequiredService<IDbContextFactory<PipelineDbContext>>(),
            sp.GetRequiredService<ITriageStore>(),
            sp.GetRequiredService<IConfigurationStore>(),
            sp.GetRequiredService<IOrchestratorRunService>(),
            sp.GetRequiredService<CodingAgent.Api.Triage.TriageTrackerOperations>(),
            sp.GetRequiredService<CodingAgent.Api.Dispatch.WorkItemRouter>(),
            Serilog.Log.Logger));
        services.AddSingleton(sp => new CodingAgent.Api.Triage.TriageIssueCreator(
            sp.GetRequiredService<ITriageStore>(),
            sp.GetRequiredService<IConfigurationStore>(),
            sp.GetRequiredService<IAgentProfileStore>(),
            sp.GetRequiredService<IProviderFactory>(),
            sp.GetRequiredService<CodingAgent.Infrastructure.Locking.IDistributedLockProvider>(),
            sp.GetRequiredService<CodingAgent.Api.Triage.TriageTrackerOperations>(),
            Serilog.Log.Logger));

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
}

/// <summary>
/// No-op database probe for integration test environments and the API service,
/// where the startup service manages DB connectivity independently.
/// </summary>
public sealed class NoOpDatabaseProbe : IDatabaseProbe
{
    public Task ProbeAsync(CancellationToken ct) => Task.CompletedTask;
}
