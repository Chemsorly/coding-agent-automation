using CodingAgent.AgentGateway;
using CodingAgent.Api.Client;
using CodingAgent.Infrastructure;
using CodingAgent.Infrastructure.Locking;
using CodingAgent.Infrastructure.Persistence;
using CodingAgent.Infrastructure.Persistence.Services;
using CodingAgent.Infrastructure.Persistence.Stores;
using CodingAgent.Kubernetes;
using CodingAgent.Orchestration;
using CodingAgent.Orchestration.Dispatch;
using CodingAgent.Orchestration.Health;
using CodingAgent.Orchestration.Registry;
using CodingAgent.Pipeline.Telemetry;
using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;
using CodingAgent.Pipeline.Services;
using k8s;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using Polly.Registry;
using Serilog;

namespace CodingAgent.Api;

/// <summary>
/// DI extension methods for CodingAgent.Api.
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
            sp.GetRequiredService<ILoggerFactory>().CreateLogger<WorkItemFallbackTransitionService>()));

        // ── IWorkItemTransitionStore (Spec 048 Phase 2 — DB isolation) ──────
        // Postgres-backed WorkItem operations (retry-count, re-queue, provider-config / issue-metadata
        // reads, throttled LastProgressAt write) for the SignalR agent hub facade. Wired only here
        // in the API host (the sole DB owner) so CodingAgent.AgentGateway carries no
        // Infrastructure.Persistence reference; the facade degrades to no-ops where it is absent.
        services.AddSingleton<IWorkItemTransitionStore>(sp => new PostgresWorkItemTransitionStore(
            sp.GetRequiredService<IDbContextFactory<PipelineDbContext>>(),
            sp.GetRequiredService<WorkItemTransitionService>()));

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
        services.AddScoped<IKeyValueStore, PostgresKeyValueStore>();

        // ── IFeedbackCommentOutbox ──────────────────────────────────────────
        // Registered as singleton (not scoped) because AgentJobLifecycleService is a singleton
        // and resolving a scoped service from the root container would throw at startup.
        // PostgresFeedbackCommentOutboxStore is safe as a singleton: it takes only
        // IDbContextFactory<PipelineDbContext> and uses a context-per-operation pattern.
        services.AddSingleton<IFeedbackCommentOutbox, PostgresFeedbackCommentOutboxStore>();

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
    /// Factory method for <see cref="IAgentRegistryService"/> extracted for testability.
    /// Called from the <c>AddApiOrchestration</c> DI registration and directly from tests
    /// to ensure tests exercise the real production code path rather than an inline copy.
    ///
    /// Returns <see cref="DistributedAgentRegistryService"/> when Redis is available.
    /// Otherwise returns the in-memory <see cref="AgentRegistryService"/>, emitting a
    /// <c>Warning</c>-level log and optionally throwing when multiple replicas are detected.
    /// </summary>
    internal static IAgentRegistryService CreateAgentRegistryService(
        IServiceProvider sp, IConfiguration config)
    {
        // TODO [WARNING]: This internal method is called directly from test code as well as from the
        // DI lambda. It has no ArgumentNullException.ThrowIfNull guards on sp or config. A test that
        // accidentally passes null for config would produce a NullReferenceException inside
        // DispatchServiceOptionsFactory.Create(config) with no actionable message. Consider adding
        // ArgumentNullException.ThrowIfNull(sp) and ArgumentNullException.ThrowIfNull(config) at
        // the top of this method.
        // See review finding [WARNING] ApiServiceCollectionExtensions.cs:169 (DotNetSpecialist review).
        var redisStore = ResolveRedisStoreOrNull(sp);
        if (redisStore is not null)
        {
            Log.Information("AgentRegistry: distributed (Redis)");
            return new DistributedAgentRegistryService(redisStore, Log.Logger);
        }

        // Replica-count guard: if multiple API replicas are running without Redis, each
        // replica maintains an independent in-memory registry — agents registered on one
        // replica are invisible to dispatchers on other replicas (split-brain).
        // ChatReplicaCount is injected by Helm from .Values.api.replicas via
        // WorkDistribution__Dispatch__ChatReplicaCount — the same value used by
        // ChatJobDispatcher.StartAsync for an analogous keepalive warning.
        var dispatchOpts = DispatchServiceOptionsFactory.Create(config);
        var replicaCount = dispatchOpts.ChatReplicaCount;
        var failOnMultiReplica = bool.TryParse(config["Api:FailOnMultiReplicaWithoutRedis"], out var f) && f;

        if (replicaCount > 1)
        {
            // TODO [WARNING]: Log.Warning is emitted here regardless of failOnMultiReplica, so the
            // fail-fast and log-only branches are asymmetric: the throw path always logs a Warning first.
            // This is benign (the warning reaches the sink before the process exits), but future readers
            // may remove the Log.Warning inside the fail-fast branch since "it's going to throw anyway",
            // silently breaking AC1 coverage for the throw path. If the two branches should stay
            // symmetric, extract Log.Warning to before the `if (failOnMultiReplica)` guard.
            // See review finding [WARNING] ApiServiceCollectionExtensions.cs (Correctness review).
            Log.Warning(
                "AgentRegistry: in-memory mode with {ReplicaCount} replicas configured — " +
                "each replica will maintain an independent agent registry (split-brain). " +
                "Agents registered on one replica will be invisible to dispatchers on other replicas. " +
                "Configure Redis (signalr.redis.connectionString) to use DistributedAgentRegistryService.",
                replicaCount);

            if (failOnMultiReplica)
                throw new InvalidOperationException(
                    "AgentRegistry: in-memory mode is not safe with multiple replicas. " +
                    "Set Redis connection string or reduce api.replicas to 1.");
        }
        else
        {
            Log.Information("AgentRegistry: in-memory (local development — Redis not configured)");
        }

        return sp.GetRequiredService<AgentRegistryService>();
    }

    /// <summary>
    /// Shared helper: resolves the Redis connection multiplexer and returns a new
    /// <see cref="CodingAgent.Orchestration.Redis.RedisStore"/> wrapping it, or
    /// <c>null</c> when no multiplexer is registered (in-memory fallback path).
    ///
    /// Called by <see cref="CreateAgentRegistryService"/>, the
    /// <see cref="IOrchestratorRunService"/> registration lambda, and the
    /// <see cref="ChatJobDispatcher"/> registration lambda — the three consumers of the
    /// Redis-or-in-memory selection pattern.
    /// </summary>
    /// <remarks>
    /// Only call this inside <c>AddSingleton</c> factory lambdas — each call creates a new
    /// <see cref="CodingAgent.Orchestration.Redis.RedisStore"/> (and calls
    /// <c>mux.GetDatabase()</c>), so request-scoped use would silently allocate extra
    /// <c>IDatabase</c> handles from the multiplexer's connection pool.
    /// </remarks>
    internal static CodingAgent.Orchestration.Redis.IRedisStore? ResolveRedisStoreOrNull(
        IServiceProvider sp)
    {
        var mux = sp.GetService<StackExchange.Redis.IConnectionMultiplexer>();
        return mux is not null
            ? new CodingAgent.Orchestration.Redis.RedisStore(mux.GetDatabase())
            : null;
    }

    /// <summary>
    /// Factory for the <c>isIssueDistributed</c> delegate used by
    /// <see cref="DistributedRunService"/>. Extracted from the DI lambda so it
    /// can be unit-tested in isolation.
    ///
    /// Implements the TOCTOU-avoiding single-query predicate: a WorkItem counts as
    /// "distributed" if it is currently active <em>or</em> recently completed within
    /// <see cref="PipelineConstants.DefaultRestartDedupCooldown"/>.
    /// </summary>
    internal static Func<string, string, CancellationToken, Task<bool>>
        CreateIsIssueDistributedDelegate(IDbContextFactory<PipelineDbContext> dbFactory)
    {
        // DefaultRestartDedupCooldown is a static readonly constant captured at factory-creation
        // time — correct for the current implementation. If it ever becomes a runtime-configurable
        // value, pass it as a parameter so the lifetime contract is explicit.
        var cooldown = PipelineConstants.DefaultRestartDedupCooldown;
        return async (issueId, providerConfigId, ct) =>
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            // Mirror WorkItemDispatchEndpoints.GetIsDistributed: single query covering
            // active status OR recently completed in one atomic DB read, eliminating
            // the TOCTOU window that existed with two sequential AnyAsync round-trips.
            // A WorkItem that transitions from active to terminal between two separate
            // reads could cause both to return false and the caller to re-dispatch.
            var activeStatuses = PipelineConstants.ActiveWorkItemStatuses;
            var since = DateTimeOffset.UtcNow - cooldown;
            return await db.WorkItems.AsNoTracking().AnyAsync(w =>
                w.IssueIdentifier == issueId &&
                w.IssueProviderConfigId == providerConfigId &&
                (activeStatuses.Contains(w.Status) ||
                 (w.CompletedAt != null && w.CompletedAt >= since)),
                ct);
        };
    }

    /// <summary>
    /// Registers orchestration services needed by the hub graph:
    /// agent registry, run service, job deduplication, dispatch infrastructure,
    /// lifecycle manager, label/token/consolidation services, and agent communication.
    /// </summary>
    public static IServiceCollection AddApiOrchestration(this IServiceCollection services, IConfiguration config)
    {
        // Serilog.ILogger for DI resolution (some services take Serilog.ILogger directly)
        services.AddSingleton(Log.Logger);

        AddOrchestrationCore(services, config);
        AddTokenVending(services);
        AddLabelServices(services);
        AddLifecycleAndConsolidation(services);
        AddKubernetes(services);
        AddDispatch(services);
        AddChatDispatch(services);

        return services;
    }

    // ── Sub-methods ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Registers the orchestration core: provider factory, agent registry, and run service.
    /// Calls <see cref="ResolveRedisStoreOrNull"/> and <see cref="CreateIsIssueDistributedDelegate"/>
    /// to keep business logic out of inline lambdas.
    /// </summary>
    private static void AddOrchestrationCore(IServiceCollection services, IConfiguration config)
    {
        // ── IProviderFactory (not registered by Infrastructure — must be explicit) ──
        services.AddSingleton<IProviderFactory>(sp =>
            new ProviderFactory(sp.GetRequiredService<IPipelineConfigStore>()));

        // ── AgentRegistryService + IAgentRegistryService ────────────────────
        // Use DistributedAgentRegistryService when Redis is available (multi-replica mode);
        // fall back to in-memory AgentRegistryService for local dev without Redis.
        services.AddSingleton<AgentRegistryService>();
        services.AddSingleton<IAgentRegistryService>(sp => CreateAgentRegistryService(sp, config));

        // ── OrchestratorRunService + IOrchestratorRunService ────────────────
        // Use DistributedRunService when Redis is available; fall back to in-memory.
        services.AddSingleton<OrchestratorRunService>(sp => new OrchestratorRunService(Log.Logger));
        services.AddSingleton<IOrchestratorRunService>(sp =>
        {
            var redisStore = ResolveRedisStoreOrNull(sp);
            if (redisStore is not null)
            {
                // IsIssueBeingProcessed: direct Postgres query — no HTTP self-call needed.
                var dbFactory = sp.GetRequiredService<IDbContextFactory<PipelineDbContext>>();
                return new DistributedRunService(
                    redisStore,
                    CreateIsIssueDistributedDelegate(dbFactory),
                    Log.Logger);
            }
            return sp.GetRequiredService<OrchestratorRunService>();
        });
    }

    /// <summary>
    /// Registers token-vending services: HTTP client defaults, named client, service, and
    /// the housekeeping hosted service.
    /// </summary>
    private static void AddTokenVending(IServiceCollection services)
    {
        // ── ITokenVendingService ─────────────────────────────────────────────
        // SocketsHttpHandler.PooledConnectionLifetime set to 90s so stale connections to
        // replaced pod IPs are recycled after a rolling update. CircuitBreaker.MinimumThroughput
        // lowered to 10 so the breaker can trip on low-traffic clients during a pod blip.
        // TODO [WARNING]: This ConfigureHttpClientDefaults call is registered independently from
        // the one in PipelineApiClientServiceCollectionExtensions.cs. If both are active in the
        // same container, ConfigurePrimaryHttpMessageHandler accumulates, and each registration
        // replaces the previous handler instance — the first SocketsHttpHandler is created and
        // immediately abandoned (minor leak until GC). Consider consolidating into a single shared
        // call. See review finding [WARNING] ApiServiceCollectionExtensions.cs:220.
        services.ConfigureHttpClientDefaults(b =>
        {
            b.ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
            {
                PooledConnectionLifetime = TimeSpan.FromSeconds(90)
            });
        });
        services.AddHttpClient("TokenVending")
            .AddStandardResilienceHandler(o => o.CircuitBreaker.MinimumThroughput = 10);
        services.AddSingleton<TokenVendingService>(sp =>
            new TokenVendingService(Log.Logger, sp.GetRequiredService<IHttpClientFactory>()));
        services.AddSingleton<ITokenVendingService>(sp => sp.GetRequiredService<TokenVendingService>());
        services.AddHostedService(sp => new TokenCacheHousekeepingService(sp.GetRequiredService<TokenVendingService>(), Log.Logger));
    }

    /// <summary>
    /// Registers label services: <see cref="ILabelService"/> and <see cref="ILabelSwapService"/>.
    /// </summary>
    private static void AddLabelServices(IServiceCollection services)
    {
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
    }

    /// <summary>
    /// Registers lifecycle, consolidation, agent communication, and run management services.
    ///
    /// NOTE: <see cref="ConsolidationServiceDependencies"/> is constructed here with 7 arguments
    /// (no <c>IConsolidationWorkspaceManager</c>, <c>IConsolidationFeedbackCache</c>, or
    /// <c>IProjectWorkspaceManager</c>). The Web host's <c>AddConsolidationServices</c> uses a
    /// 9-argument overload. These are intentionally different — do NOT unify them.
    /// </summary>
    private static void AddLifecycleAndConsolidation(IServiceCollection services)
    {
        // ── PipelineRunLifecycleService — implements IChangeNotifier + IChatNotifier ──
        services.AddSingleton<PipelineRunLifecycleService>(sp => new PipelineRunLifecycleService(
            sp.GetRequiredService<IPipelineRunHistoryService>(),
            sp.GetRequiredService<IOrchestratorRunService>(),
            Log.Logger,
            sp.GetService<IAgentCancellationSender>()));
        services.AddSingleton<IChangeNotifier>(sp => sp.GetRequiredService<PipelineRunLifecycleService>());
        services.AddSingleton<IChatNotifier>(sp => sp.GetRequiredService<PipelineRunLifecycleService>());

        // ── IConsolidationService ────────────────────────────────────────────
        // IMPORTANT: intentionally 7-argument form — not the 9-argument Web overload.
        services.AddSingleton<IConsolidationService>(sp => new ConsolidationService(
            new ConsolidationServiceDependencies(
                Log.Logger,
                new PipelineConfiguration(),
                sp.GetRequiredService<IProjectStore>(),
                sp.GetRequiredService<IPipelineRunHistoryService>(),
                sp.GetRequiredService<IConsolidationRunStore>(),
                sp.GetRequiredService<IHarnessSuggestionStore>(),
                sp.GetRequiredService<IProviderConfigStore>())));

        // ── IAgentCommunication → SignalRAgentCommunication ──────────────────
        // Registered before ModelFetchService which depends on it.
        services.AddSingleton<IAgentCommunication>(sp =>
            new SignalRAgentCommunication(
                sp.GetRequiredService<Microsoft.AspNetCore.SignalR.IHubContext<AgentHub, IAgentHubClient>>()));

        // ── ModelFetchService ────────────────────────────────────────────────
        services.AddSingleton<ModelFetchService>(sp => new ModelFetchService(
            sp.GetRequiredService<IAgentRegistryService>(),
            sp.GetRequiredService<IAgentCommunication>(),
            Log.Logger));

        // ── ConsolidationBadgeService ────────────────────────────────────────
        services.AddSingleton<ConsolidationBadgeService>();

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
                Log.Logger,
                sp.GetService<IJobCleanupStrategy>(),
                sp.GetRequiredService<IWorkItemFallbackTransitionService>())));

        // ── WorkItemStatusTransitionService (issue #2914) ──────────────────────
        // Encapsulates the compound status-transition orchestration extracted from
        // WorkItemAgentEndpoints.PostStatus: infra-recovery guard, pre-read idempotency guard,
        // TransitionDetailedAsync, lifecycle dispatch, and telemetry fire-and-forget.
        // Placed in CodingAgent.Api (not CodingAgent.Orchestration) to respect the
        // Orchestration_ShouldNot_ReferenceInfrastructurePersistenceAssembly arch boundary.
        // Depends on IRunLifecycleManager — registered in this same sub-method above.
        services.AddSingleton(sp => new WorkItemStatusTransitionService(
            sp.GetRequiredService<WorkItemTransitionService>(),
            sp.GetRequiredService<IRunLifecycleManager>(),
            sp.GetRequiredService<IDbContextFactory<PipelineDbContext>>()));

        // ── IConsolidationJobPreparationService ────────────────────────────
        // Required by AssignmentEnricher to resolve provider configs and vend short-lived tokens
        // at assignment time (GET /api/work-items/{id}/assignment).
        // Also used by ReportConsolidationComplete hub handling.
        services.AddSingleton<IConsolidationJobPreparationService>(sp =>
            new ConsolidationJobPreparationService(
                sp.GetRequiredService<IProviderConfigStore>(),
                sp.GetRequiredService<IProjectStore>(),
                sp.GetRequiredService<ITokenVendingService>(),
                Log.Logger,
                sp.GetRequiredService<IAgentProfileStore>(),
                sp.GetRequiredService<IPipelineConfigStore>()));
    }

    /// <summary>
    /// Registers Kubernetes client, job client, cleanup strategy, job template store,
    /// and database maintenance service.
    /// </summary>
    private static void AddKubernetes(IServiceCollection services)
    {
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
    }

    /// <summary>
    /// Registers the dispatch subsystem: resolvers, infrastructure, enricher, and
    /// the synchronous dispatch services used by POST /api/work-items/dispatch.
    /// </summary>
    private static void AddDispatch(IServiceCollection services)
    {
        // ── DispatchInfrastructure + AssignmentEnricher (issue #2171) ────────
        // DispatchInfrastructure aggregates ITokenVendingService, IProviderFactory,
        // ILabelService, and DispatchResolutionService. Used by AssignmentEnricher to
        // fetch fresh provider configs, steering, QGs, and issue context at assignment time.
        // Cache disabled on IConfigurationStore (registered above) so every GetAssignment
        // call sees the latest steering and QG config from the DB.
        // The API host does not have a IPipelineApiWorkItemClient in this host,
        // so includeWorkItemClient is false (workItemClient = null).
        services.AddDispatchResolutionServices(includeWorkItemClient: false);

        // ── ConsolidationTemplateResolver ──────────────────────────────────────
        // Registered as a standalone singleton so AssignmentEnricher.InjectProjectSecretsAsync
        // can delegate template-ownership resolution to it (issue #2914), eliminating the
        // reimplemented loop that previously mirrored its behaviour inline.
        // Takes only IProjectStore — already registered above as a singleton.
        services.AddSingleton(sp => new ConsolidationTemplateResolver(
            sp.GetRequiredService<IProjectStore>()));
        services.AddSingleton(sp => new AssignmentEnricher(
            sp.GetRequiredService<DispatchInfrastructure>(),
            sp.GetRequiredService<IAgentProfileStore>(),
            sp.GetRequiredService<IConsolidationJobPreparationService>(),
            sp.GetRequiredService<IProjectStore>(),
            sp.GetRequiredService<ConsolidationTemplateResolver>(),
            Log.Logger));

        // ── Synchronous dispatch services (POST /api/work-items/dispatch) ────────────────────
        // DispatchLifecycleService — shared PVC-selection lock + K8s Job creation lifecycle.
        // DispatchTemplateResolver — agent-selector → JobTemplate fallback resolution.
        // DispatchStateBuilder     — builds concurrency map and PVC availability state.
        // Used by WorkItemDispatchLoop (Scheduler) via POST /api/work-items/{id}/dispatch.
        services.AddSingleton<CodingAgent.Api.Dispatch.DispatchLifecycleService>(sp =>
        {
            var jobClient = sp.GetService<IKubernetesJobClient>();
            var options = DispatchServiceOptionsFactory.Create(sp.GetRequiredService<IConfiguration>());
            if (jobClient is null)
                throw new InvalidOperationException(
                    "API: IKubernetesJobClient is not registered. " +
                    "POST /api/work-items/dispatch requires a Kubernetes client. " +
                    "Register IKubernetesJobClient in DI or disable the synchronous dispatch endpoint.");
            return new CodingAgent.Api.Dispatch.DispatchLifecycleService(
                jobClient,
                sp.GetRequiredService<WorkItemTransitionService>(),
                options);
        });
        services.AddSingleton<CodingAgent.Api.Dispatch.DispatchTemplateResolver>(sp =>
            new CodingAgent.Api.Dispatch.DispatchTemplateResolver(
                sp.GetService<IAgentProfileStore>(),
                sp.GetRequiredService<JobTemplateStore>()));
        services.AddSingleton<CodingAgent.Api.Dispatch.DispatchStateBuilder>(sp =>
            new CodingAgent.Api.Dispatch.DispatchStateBuilder(
                sp.GetRequiredService<IDbContextFactory<PipelineDbContext>>(),
                sp.GetRequiredService<CodingAgent.Api.Dispatch.DispatchLifecycleService>(),
                sp.GetRequiredService<JobTemplateStore>(),
                sp.GetRequiredService<CodingAgent.Api.Dispatch.DispatchTemplateResolver>(),
                DispatchServiceOptionsFactory.Create(sp.GetRequiredService<IConfiguration>())));

        // ── DispatchWorkItemService (issue #2743) ─────────────────────────────────────────────
        // Shared helpers for the two synchronous dispatch handlers (DispatchWorkItem and
        // DispatchPendingWorkItem): concurrency-snapshot query, gate block, entity factory,
        // and unique-violation fallback. Singleton — stateless, depends only on JobTemplateStore.
        services.AddSingleton<CodingAgent.Api.Dispatch.DispatchWorkItemService>(sp =>
            new CodingAgent.Api.Dispatch.DispatchWorkItemService(
                sp.GetRequiredService<JobTemplateStore>()));
    }

    /// <summary>
    /// Registers chat dispatch services: model fetch job service, ChatJobDispatcher hosted
    /// service, and the IChatJobDispatcher forwarding registration.
    /// Calls <see cref="ResolveRedisStoreOrNull"/> — the third consumer of the shared helper.
    /// </summary>
    private static void AddChatDispatch(IServiceCollection services)
    {
        // ── WorkItemMetricsBackgroundService ──────────────────────────────────────────────────
        // Spec 047: Removed from API hosted services — replaced by WorkItemCountsService in
        // CodingAgent.Scheduler. WorkItemCountsService polls GET /api/work-items/counts-by-status
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
        //
        // ChatHeartbeatTracker and ChatSessionWatcher are internal to CodingAgent.AgentGateway.
        // The public ChatJobDispatcher constructor accepts IRedisStore? and constructs the
        // collaborators internally, so CodingAgent.Api does not need to reference internal types.
        services.AddSingleton<ChatJobDispatcher>(sp =>
        {
            var options = DispatchServiceOptionsFactory.Create(sp.GetRequiredService<IConfiguration>());
            options.ValidateAndClamp(Log.Logger);
            var redisStore = ResolveRedisStoreOrNull(sp);
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
