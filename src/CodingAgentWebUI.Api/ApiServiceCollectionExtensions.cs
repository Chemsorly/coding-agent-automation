using CodingAgentWebUI.Hub;
using CodingAgentWebUI.Infrastructure;
using CodingAgentWebUI.Infrastructure.Locking;
using CodingAgentWebUI.Infrastructure.Persistence;
using CodingAgentWebUI.Infrastructure.Persistence.Services;
using CodingAgentWebUI.Infrastructure.Persistence.Stores;
using CodingAgentWebUI.Kubernetes;
using CodingAgentWebUI.Orchestration;
using CodingAgentWebUI.Orchestration.Dispatch;
using CodingAgentWebUI.Orchestration.Health;
using CodingAgentWebUI.Orchestration.Registry;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.LeaderElection;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Pipeline.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
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

        // ── IKeyValueStore (scoped, from Spec 041) ──────────────────────────
        services.AddScoped<IKeyValueStore, EfKeyValueStore>();

        // ── IDatabaseProbe (no-op — real DB connectivity is handled by DatabaseStartupService) ─
        services.AddSingleton<IDatabaseProbe, NoOpDatabaseProbe>();

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
        services.AddSingleton<AgentRegistryService>();
        services.AddSingleton<IAgentRegistryService>(sp => sp.GetRequiredService<AgentRegistryService>());

        // ── OrchestratorRunService + IOrchestratorRunService ────────────────
        services.AddSingleton<OrchestratorRunService>(sp => new OrchestratorRunService(Log.Logger));
        services.AddSingleton<IOrchestratorRunService>(sp => sp.GetRequiredService<OrchestratorRunService>());

        // ── JobDeduplicationGuardService ────────────────────────────────────
        services.AddSingleton<JobDeduplicationGuardService>();

        // ── JobQueueDrainService (needed by AgentHubFacadeDependencies) ─────
        // Registered with explicit factory because the constructor is internal.
        // IJobDispatcher and IConsolidationDispatchService are no-op stubs because
        // Legacy queue dispatch is dead after Spec 041.
        // TODO(Spec 043/044, same branch): remove when hub moves out of the monolith.
        services.AddSingleton<IJobDispatcher>(_ => new NullJobDispatcher());
        services.AddSingleton<IConsolidationDispatchService>(_ => new NullConsolidationDispatchService());
        services.AddSingleton<IShutdownSignal>(new ShutdownSignal());
        services.AddSingleton(sp => new JobQueueDrainService(
            new JobQueueDrainDependencies(
                sp.GetRequiredService<JobDeduplicationGuardService>(),
                sp.GetRequiredService<IAgentRegistryService>(),
                sp.GetRequiredService<IJobDispatcher>(),
                sp.GetRequiredService<IConfigurationStore>(),
                sp.GetRequiredService<IConsolidationDispatchService>(),
                sp.GetRequiredService<IShutdownSignal>(),
                Log.Logger,
                sp.GetService<IConsolidationRunStore>())));

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
            sp.GetRequiredService<AgentRegistryService>(),
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
                sp.GetRequiredService<AgentRegistryService>(),
                sp.GetRequiredService<ILabelService>(),
                sp.GetRequiredService<JobDeduplicationGuardService>(),
                Log.Logger,
                sp.GetRequiredService<WorkItemTransitionService>(),
                sp.GetService<IJobCleanupStrategy>(),
                sp.GetRequiredService<IWorkItemFallbackTransitionService>())));

        // NO DatabaseMaintenanceService (Req 5.6a — ungated sweep on every API replica)
        // NO WorkItemMetricsBackgroundService (Req 5.6a — duplicate static telemetry callback)

        // ── DispatchLifecycleService (API copy, EF-coupled) ─────────────────────────────
        // Used by ConsolidationWorkItemDispatchService and ModelFetchJobService.
        // Relocated from CodingAgentWebUI.Orchestration (Spec 043 Task 8b + 9).
        services.AddSingleton<CodingAgentWebUI.Api.Dispatch.DispatchLifecycleService>(sp =>
        {
            var options = DispatchServiceOptionsFactory.Create(sp.GetRequiredService<IConfiguration>());
            if (string.IsNullOrEmpty(options.AgentMasterApiKey))
            {
                Serilog.Log.Warning(
                    "AddApiOrchestration: AGENT_API_KEY is not configured. " +
                    "ModelFetchJobService and ConsolidationWorkItemDispatchService will use an empty master key " +
                    "for HMAC derivation — this is a security misconfiguration.");
            }
            return new CodingAgentWebUI.Api.Dispatch.DispatchLifecycleService(
                sp.GetRequiredService<IKubernetesJobClient>(),
                sp.GetRequiredService<WorkItemTransitionService>(),
                options);
        });

        // ── DispatchStateBuilder (API copy, EF-coupled) ─────────────────────────────────
        // Used by ConsolidationWorkItemDispatchService.
        services.AddSingleton<CodingAgentWebUI.Api.Dispatch.DispatchStateBuilder>(sp => new CodingAgentWebUI.Api.Dispatch.DispatchStateBuilder(
            sp.GetRequiredService<IDbContextFactory<PipelineDbContext>>(),
            sp.GetRequiredService<CodingAgentWebUI.Api.Dispatch.DispatchLifecycleService>(),
            sp.GetRequiredService<JobTemplateStore>(),
            new CodingAgentWebUI.Api.Dispatch.DispatchTemplateResolver(
                sp.GetService<IAgentProfileStore>(),
                sp.GetRequiredService<JobTemplateStore>()),
            DispatchServiceOptionsFactory.Create(sp.GetRequiredService<IConfiguration>())));

        // ── ConsolidationWorkItemDispatchService (relocated from Orchestration, Spec 043 Task 9) ──
        // Registers as a hosted service in the API. Handles consolidation work items (TaskType=Consolidation).
        // ILabelSwapService accessibility: LabelSwapService is internal sealed in Orchestration,
        // but CodingAgentWebUI.Api is in InternalsVisibleTo — registered above as ILabelSwapService.
        services.AddHostedService(sp => new CodingAgentWebUI.Api.Dispatch.ConsolidationWorkItemDispatchService(
            new CodingAgentWebUI.Api.Dispatch.ConsolidationWorkItemDispatchServiceDependencies(
                sp.GetRequiredService<IDbContextFactory<PipelineDbContext>>(),
                sp.GetRequiredService<ILeaderElectionService>(),
                sp.GetRequiredService<CodingAgentWebUI.Api.Dispatch.DispatchLifecycleService>(),
                sp.GetRequiredService<JobTemplateStore>(),
                sp.GetRequiredService<IConfiguration>(),
                sp.GetRequiredService<WorkItemTransitionService>(),
                sp.GetService<IConsolidationRunStore>(),
                sp.GetService<IConsolidationService>(),
                sp.GetService<IConsolidationJobPreparationService>(),
                sp.GetService<IPipelineConfigStore>(),
                sp.GetService<IProjectStore>(),
                sp.GetService<IAgentProfileStore>(),
                sp.GetRequiredService<CodingAgentWebUI.Api.Dispatch.DispatchStateBuilder>())));

        // ── ModelFetchJobService (relocated from Orchestration monolith, Spec 043 Task 9) ──
        // Registers as a singleton in the API. The API has K8s RBAC for batch/jobs (Req 9.3b).
        services.AddSingleton<ModelFetchJobService>(sp => new ModelFetchJobService(
            new ModelFetchJobDependencies(
                sp.GetRequiredService<IKubernetesJobClient>(),
                sp.GetRequiredService<JobTemplateStore>(),
                DispatchServiceOptionsFactory.Create(sp.GetRequiredService<IConfiguration>()),
                sp.GetRequiredService<IPipelineConfigStore>(),
                sp.GetRequiredService<ModelFetchService>(),
                Logger: Log.Logger)));

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
