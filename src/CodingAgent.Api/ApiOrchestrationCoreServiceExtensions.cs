using CodingAgent.Infrastructure.Persistence;
using CodingAgent.Kubernetes;
using CodingAgent.Orchestration;
using CodingAgent.Orchestration.Dispatch;
using CodingAgent.Orchestration.Redis;
using CodingAgent.Orchestration.Registry;
using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Serilog;
using StackExchange.Redis;

namespace CodingAgent.Api;

/// <summary>
/// DI extension methods for the orchestration core: registry, run service, and
/// cross-cutting helpers extracted for testability.
/// </summary>
internal static class ApiOrchestrationCoreServiceExtensions
{
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
        var redisStore = sp.GetService<IRedisStore>();
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
    /// Called exactly once — from the <c>AddSingleton&lt;IRedisStore?&gt;</c> registration
    /// in <see cref="AddOrchestrationCore"/>. All Redis consumers in this host resolve
    /// <c>IRedisStore</c> via <c>sp.GetService&lt;IRedisStore&gt;()</c> instead of calling
    /// this method directly.
    /// </summary>
    /// <remarks>
    /// The singleton semantics ensure <c>mux.GetDatabase()</c> is called at most once,
    /// and the resulting <see cref="CodingAgent.Orchestration.Redis.RedisStore"/> instance
    /// is shared across all consumers in the process lifetime.
    /// </remarks>
    private static IRedisStore? ResolveRedisStoreOrNull(IServiceProvider sp)
    {
        ArgumentNullException.ThrowIfNull(sp);
        var mux = sp.GetService<IConnectionMultiplexer>();
        return mux is not null
            ? new RedisStore(mux.GetDatabase())
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
            // Mirror WorkItemStore.IsDistributedAsync: single query covering
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
    /// Registers the orchestration core: IRedisStore singleton, provider factory, agent
    /// registry, and run service.
    /// Calls <see cref="CreateIsIssueDistributedDelegate"/> to keep business logic out of
    /// inline lambdas.
    /// </summary>
    internal static void AddOrchestrationCore(IServiceCollection services, IConfiguration config)
    {
        // ── IRedisStore singleton ─────────────────────────────────────────────
        // Registered once here so every Redis consumer in this host resolves the same
        // IRedisStore instance via sp.GetService<IRedisStore>() instead of each lambda
        // calling ResolveRedisStoreOrNull(sp) and creating a separate RedisStore handle.
        // The factory returns null when no IConnectionMultiplexer is registered (i.e. when
        // the SignalR Redis backplane is not configured), so callers that accept IRedisStore?
        // fall back to their in-memory equivalents as before.
        // IConnectionMultiplexer is registered by AddApiSignalR (called after AddApiOrchestration
        // in Program.cs) but DI lambdas are lazy — the multiplexer is present in the container
        // before any singleton factory that resolves IRedisStore is first invoked.
        services.AddSingleton<IRedisStore>(sp => ResolveRedisStoreOrNull(sp)!);

        // ── Five services shared with the Scheduler host ──────────────────────
        // IProviderFactory, IConsolidationService, IDependencyChecker,
        // IDispatchOrchestrationService, ConsolidationStalenessService.
        // Spec 051 2.3b, 2.3c, 2.4, 2.4b; issue #3592.
        // Prerequisite call: DispatchInfrastructure is registered later in AddDispatch →
        // AddDispatchResolutionServices, which runs before AddDispatch resolves these factories
        // (DI lambdas are lazy — registration order does not constrain resolution order).
        services.AddSharedOrchestrationServices();

        // Spec 051 2.1c: the Settings provider checks run here, with the stored credentials.
        services.AddTransient(sp => new CodingAgent.Infrastructure.GitHub.GitHubValidationService(sp.GetRequiredService<IProviderFactory>()));
        services.AddTransient<CodingAgent.Infrastructure.GitLab.GitLabValidationService>();

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
            var redisStore = sp.GetService<IRedisStore>();
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
}
