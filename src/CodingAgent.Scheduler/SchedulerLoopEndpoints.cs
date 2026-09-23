using CodingAgent.Api.Client;
using CodingAgent.Orchestration.Redis;
using CodingAgent.Pipeline;
using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;
using CodingAgent.Pipeline.Services;
using System.Text.Json;
using ILogger = Serilog.ILogger;
using ILeaderGate = CodingAgent.Pipeline.Interfaces.ILeaderGate;

namespace CodingAgent.Scheduler;

/// <summary>
/// Minimal API endpoints for loop control, hosted by the Scheduler on port 8080.
/// Called by the WebUI to start/stop/resume the loop and poll status.
///
/// Authentication: X-Api-Key header must match AGENT_API_KEY env var — same key as the API.
/// </summary>
public static class SchedulerLoopEndpoints
{
    /// <summary>
    /// Maps loop control endpoints and wires the OnChange cache update.
    /// Must be called after PipelineLoopService and LoopStatusCache are registered in DI.
    /// </summary>
    public static void MapSchedulerLoopEndpoints(this IEndpointRouteBuilder app)
    {
        var apiKey = app.ServiceProvider.GetRequiredService<IConfiguration>()
            .GetValue<string>("AGENT_API_KEY");

        // Wire cache update on loop state change — via the DI-scoped LoopStatusCache singleton
        var cache = app.ServiceProvider.GetRequiredService<LoopStatusCache>();
        var loopService = app.ServiceProvider.GetRequiredService<IPipelineLoopService>();
        loopService.OnChange += () => cache.Update(BuildDto(loopService));

        // Emit a warning when the API key filter will be in fail-open mode
        if (string.IsNullOrEmpty(apiKey))
            Serilog.Log.Warning("SchedulerLoopEndpoints: AGENT_API_KEY is not configured — loop endpoints are unauthenticated");

        var group = app.MapGroup("/loop")
            .AddEndpointFilter(new ApiKeyFilter(apiKey ?? ""));

        group.MapGet("/status", GetLoopStatus);
        group.MapPost("/start", StartLoop);
        group.MapPost("/stop", StopLoop);
        group.MapPost("/resume", ResumeLoop);
    }

    // ── Handlers ─────────────────────────────────────────────────────────────

    internal static async Task<IResult> GetLoopStatus(IPipelineLoopService loopService, LoopStatusCache cache)
    {
        // Serve from the DI-singleton cache — avoids lock contention on the loop service.
        // On the leader, the local in-memory value is returned immediately (no Redis round-trip).
        // On non-leader pods, falls back to the Redis snapshot written by the leader, then to
        // building on demand when neither cache source has a value yet.
        var dto = await cache.ReadAsync() ?? BuildDto(loopService);
        return Results.Ok(dto);
    }

    internal static async Task<IResult> StartLoop(
        IPipelineLoopService loopService,
        IPipelineApiConfigClient configClient,
        CancellationToken ct)
    {
        var started = await loopService.StartLoopAsync();
        if (started)
        {
            // Persist ClosedLoopAutoStart=true so the Scheduler auto-starts on next boot
            await configClient.UpdatePipelineConfigAsync(c => c with { ClosedLoopAutoStart = true }, ct);
        }
        var error = started ? null
            : loopService.ValidationErrors.Count > 0 ? "Loop failed to start due to validation errors."
            : loopService.IsLoopActive ? "Loop is already active."
            : "A manual run is in progress. Wait for it to complete.";
        return Results.Ok(new LoopStartResultDto(started, error));
    }

    internal static async Task<IResult> StopLoop(
        IPipelineLoopService loopService,
        IPipelineApiConfigClient configClient,
        CancellationToken ct)
    {
        loopService.StopLoop();
        await configClient.UpdatePipelineConfigAsync(c => c with { ClosedLoopAutoStart = false }, ct);
        return Results.NoContent();
    }

    internal static IResult ResumeLoop(IPipelineLoopService loopService)
    {
        loopService.ResumeLoop();
        return Results.NoContent();
    }

    internal static LoopStatusDto BuildDto(IPipelineLoopService svc) => new(
        svc.IsLoopActive,
        svc.StatusMessage,
        svc.CurrentIssueIdentifier,
        svc.ProcessedCount,
        svc.FailedCount,
        svc.QueueCount,
        svc.IsCircuitBroken,
        svc.LastPollError,
        svc.CurrentCycleTemplateIndex,
        svc.CurrentCycleTemplateCount,
        svc.ValidationErrors,
        svc.TemplateStatuses);

    // ── API key filter ────────────────────────────────────────────────────────

    private sealed class ApiKeyFilter : IEndpointFilter
    {
        private readonly string _expectedKey;
        public ApiKeyFilter(string expectedKey) => _expectedKey = expectedKey;

        public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext ctx, EndpointFilterDelegate next)
        {
            if (string.IsNullOrEmpty(_expectedKey))
            {
                // No key configured — fail closed to prevent unauthenticated access in production.
                // If AGENT_API_KEY is missing (misconfiguration or partial local config), returning
                // 503 is safer than allowing unrestricted loop control to anyone on the network.
                return Results.Problem(
                    title: "Service Unavailable",
                    detail: "AGENT_API_KEY is not configured. Loop control endpoints are disabled.",
                    statusCode: StatusCodes.Status503ServiceUnavailable);
            }

            if (!ctx.HttpContext.Request.Headers.TryGetValue("X-Api-Key", out var provided)
                || provided != _expectedKey)
            {
                return Results.Unauthorized();
            }
            return await next(ctx);
        }
    }
}

/// <summary>
/// DI-singleton that holds the most-recently-built <see cref="LoopStatusDto"/> snapshot.
/// Updated via <see cref="PipelineLoopService.OnChange"/>; served by the /loop/status handler.
///
/// In single-replica deployments (or when Redis is not configured), operates purely in-process
/// via the volatile <c>_value</c> field. In multi-replica deployments, the leader pod writes
/// each snapshot to Redis on <see cref="Update"/>; non-leader pods read it from Redis in
/// <see cref="ReadAsync"/> so all replicas serve a consistent status message.
///
/// Leader awareness: when <see cref="ILeaderGate"/> is provided and this pod is not the leader,
/// <see cref="ReadAsync"/> skips the local fast-path and reads from Redis directly. This is
/// necessary because <c>AutoStartSchedulerLoopAsync</c> calls <c>StartLoopAsync</c> on every
/// pod at boot, which fires <c>OnChange</c> and populates the local cache with the initial
/// "Loop starting…" snapshot on every pod — not just the leader. Without the leader-aware
/// read path, the non-leader's local stale value would always shadow the Redis snapshot.
///
/// Using a registered singleton rather than a static field ensures state is scoped to a
/// single DI container — preventing test isolation issues when multiple
/// <see cref="Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory{T}"/> instances
/// run in the same process.
/// </summary>
public sealed class LoopStatusCache
{
    internal const string RedisKey = "scheduler:loop-status";

    // RedisTtl is set to 600s to ensure the Redis key survives the full idle DelayOrStop wait
    // between cycles. During the wait, no OnChange fires on the leader; without a TTL longer
    // than the poll interval the key would expire and non-leader pods would fall back to their
    // own stale local state for the remainder of the cycle.
    // TODO [WARNING]: This constant does not adapt to the configured ClosedLoopPollInterval.
    // ClosedLoopPollInterval has no enforced server-side upper bound (the UI max="600" is
    // bypassable via direct API calls), so a deployment with an interval > 600s (e.g. 900s)
    // would still expire the Redis key before the next cycle, reintroducing the original bug.
    // Fix: derive the TTL dynamically from the actual configured interval (e.g. 2 × interval)
    // by passing the interval into LoopStatusCache, or implement a periodic leader refresh timer
    // (every ~10s) as the issue describes. Note: the comment below incorrectly states "default
    // 300s poll interval" — PipelineConstants.DefaultClosedLoopPollInterval is 60s, not 300s,
    // and the "maximum configurable poll interval (300s)" is also incorrect — the interval is
    // uncapped server-side and the UI allows up to 600s.
    private static readonly TimeSpan RedisTtl = TimeSpan.FromSeconds(600);

    private readonly IRedisStore? _store;
    private readonly ILeaderGate? _leaderGate;
    private readonly ILogger _logger;
    // TODO [WARNING]: _value is not declared volatile. All accesses go through Volatile.Read/Write,
    // which is correct, but the plain field declaration leaves no compiler-enforced guard against a
    // future caller reading the field directly (e.g. `return _value;`) without Volatile.Read,
    // silently bypassing the required memory ordering. Declaring the field `volatile` would make
    // the intent self-enforcing and prevent accidental direct reads.
    private LoopStatusDto? _value;

    /// <summary>
    /// Creates a cache instance. All parameters are optional to preserve backward
    /// compatibility with call sites that use <c>new LoopStatusCache()</c> (e.g., tests).
    /// </summary>
    /// <param name="redisStore">Redis store for cross-pod sharing. Null = in-process only.</param>
    /// <param name="leaderGate">
    /// Leader election gate used to decide whether to skip the local fast-path.
    /// Null = always use local value (single-replica or test environments).
    /// </param>
    /// <param name="logger">Logger for Redis write/read failures. Null = static Serilog.Log.Logger.</param>
    public LoopStatusCache(IRedisStore? redisStore = null, ILeaderGate? leaderGate = null, ILogger? logger = null)
    {
        _store = redisStore;
        _leaderGate = leaderGate;
        _logger = logger ?? Serilog.Log.Logger;
    }

    /// <summary>
    /// Stores a new snapshot locally and publishes it to Redis (fire-and-forget).
    /// Thread-safe via reference replacement. Redis write failures are logged and swallowed —
    /// they must not propagate since <see cref="PipelineLoopService.OnChange"/> is synchronous.
    /// <para>
    /// The local <c>_value</c> is always updated regardless of leader status, so <see cref="Read"/>
    /// always returns current state. The Redis write is skipped on non-leader pods to prevent
    /// stale boot-time snapshots from overwriting the leader's correct Redis value.
    /// </para>
    /// </summary>
    public void Update(LoopStatusDto dto)
    {
        // Always update the local in-memory snapshot — required so Read() returns current state
        // and so ReadAsync()'s local fast-path works correctly after a leadership transition.
        Volatile.Write(ref _value, dto);

        if (_store is null) return;

        // Skip the Redis write on non-leader pods. AutoStartSchedulerLoopAsync calls StartLoopAsync
        // on every pod at boot, which fires OnChange → Update() with the stale "🔄 Loop starting…"
        // DTO. Without this guard, a non-leader restart overwrites the leader's correct Redis
        // snapshot, causing all pods to briefly serve "Loop starting…" until the leader's next
        // OnChange fires. Single-replica / no-leader-gate environments are unaffected (_leaderGate
        // is null → guard doesn't fire → Redis write proceeds as before).
        // TODO [WARNING]: TOCTOU race — IsLeader is checked synchronously here, but the actual
        // Redis write is dispatched as a fire-and-forget Task. If leadership is revoked between
        // this guard check and the time the thread-pool runs SetAsync (e.g. under GC pause or
        // thread-pool saturation), a former-leader pod will write its stale snapshot to Redis.
        // The window is narrow (sub-millisecond under normal conditions) but theoretically
        // possible. Fixing it properly would require a fundamentally different write pattern
        // (e.g. passing a leadership token into SetAsync, or using a conditional Redis write).
        if (_leaderGate is not null && !_leaderGate.IsLeader) return;

        // Fire-and-forget: OnChange is event Action? (synchronous), so we cannot await.
        // ContinueWith(OnlyOnFaulted) logs any Redis failure without blocking the caller.
        _ = _store.SetAsync(RedisKey, JsonSerializer.Serialize(dto, PipelineJsonOptions.Default), RedisTtl)
            .ContinueWith(
                t => _logger.Warning(t.Exception, "LoopStatusCache: Redis write failed for key {Key}", RedisKey),
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted,
                TaskScheduler.Default);
    }

    /// <summary>
    /// Returns the current snapshot, checking local memory first when this pod is the leader
    /// (fast path — no Redis round-trip), then falling back to the Redis snapshot written by
    /// the leader pod, then returning null (leader) or a neutral DTO (non-leader).
    /// <para>
    /// When <see cref="ILeaderGate"/> is provided and <see cref="ILeaderGate.IsLeader"/> is
    /// false, the local fast-path is skipped so that non-leader pods always serve the Redis
    /// snapshot rather than their own stale initial state.
    /// </para>
    /// <para>
    /// When the Redis snapshot is absent (key expired or unavailable) and this pod is a
    /// non-leader, a neutral DTO is returned ("⏳ Waiting for leader status…", IsLoopActive=false)
    /// rather than null. This prevents <c>GetLoopStatus</c> from falling back to
    /// <c>BuildDto(loopService)</c>, which would serve the non-leader's own stale "Loop starting…"
    /// local state. On leader pods the null return is preserved so <c>GetLoopStatus</c> can still
    /// call <c>BuildDto(loopService)</c> during the pre-first-cycle startup window.
    /// </para>
    /// </summary>
    public async Task<LoopStatusDto?> ReadAsync()
    {
        // Local fast path — only taken when this pod is the leader (or no leader gate is
        // configured, e.g. single-replica / test environments). The leader's local value is
        // always up-to-date because OnChange fires on every cycle. Non-leader pods must skip
        // this path: their local value is frozen at the "Loop starting…" snapshot set by
        // AutoStartSchedulerLoopAsync and never updated, because ExecuteAsync on non-leaders
        // blocks in the leader-wait loop without running the cycle.
        var isLeader = _leaderGate is null || _leaderGate.IsLeader;
        if (isLeader)
        {
            var local = Volatile.Read(ref _value);
            if (local is not null) return local;
        }

        // Redis fallback — serves the leader's snapshot to non-leader pods.
        // A newly-elected leader pod will serve this Redis path (potentially stale, from the
        // prior leader) rather than its own up-to-date local _value until it fires the next
        // OnChange and writes a fresh snapshot. The window is bounded by the next cycle.
        // TODO [WARNING]: If _leaderGate is non-null but _store is null (leader gate present,
        // no Redis store configured), a confirmed non-leader pod returns null here instead of
        // the neutral DTO, causing GetLoopStatus to fall back to BuildDto(loopService) and serve
        // the pod's own stale "Loop starting…" state. This combination (leader election without
        // a shared Redis store) is unusual in practice because leader election normally implies a
        // shared backing store, but if it can occur a guard/assertion documenting the invariant
        // would make the intent explicit. No test covers this path.
        if (_store is null) return null;
        try
        {
            var json = await _store.GetAsync(RedisKey);
            if (json is not null)
                return JsonSerializer.Deserialize<LoopStatusDto>(json, PipelineJsonOptions.Lenient);

            // Redis returned null (key expired or not yet written). Return a neutral DTO for
            // non-leader pods so GetLoopStatus never falls back to BuildDto(loopService) and
            // serves stale "Loop starting…" state. Return null for leader pods so GetLoopStatus
            // still calls BuildDto(loopService) during the pre-first-cycle startup window.
            // TODO [WARNING]: TOCTOU between isLeader (captured above) and BuildNeutralDtoIfNonLeader()
            // (which re-queries _leaderGate.IsLeader directly). If leadership changes between those two
            // evaluations, the two checks may disagree: the method could skip the local fast-path
            // (treating the pod as non-leader) but BuildNeutralDtoIfNonLeader sees IsLeader==true and
            // returns null, causing GetLoopStatus to fall back to BuildDto(loopService) and serve the
            // stale local state that the non-leader guard was designed to prevent. To close this gap,
            // pass the captured `isLeader` value into BuildNeutralDtoIfNonLeader instead of re-reading
            // _leaderGate.IsLeader. The same race applies to the catch block below.
            return BuildNeutralDtoIfNonLeader();
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "LoopStatusCache: Redis read failed for key {Key} — falling back", RedisKey);
            // Same neutral DTO for non-leaders when Redis is unavailable.
            return BuildNeutralDtoIfNonLeader();
        }
    }

    /// <summary>
    /// Returns a neutral DTO when this pod is a confirmed non-leader, or null otherwise.
    /// Used by <see cref="ReadAsync"/> to prevent non-leader pods from serving stale local state.
    /// </summary>
    private LoopStatusDto? BuildNeutralDtoIfNonLeader()
    {
        if (_leaderGate is not null && !_leaderGate.IsLeader)
            return new LoopStatusDto(
                IsLoopActive: false,
                StatusMessage: "⏳ Waiting for leader status…",
                CurrentIssueIdentifier: null,
                ProcessedCount: 0,
                FailedCount: 0,
                QueueCount: 0,
                IsCircuitBroken: false,
                LastPollError: null,
                CurrentCycleTemplateIndex: 0,
                CurrentCycleTemplateCount: 0,
                ValidationErrors: [],
                TemplateStatuses: new Dictionary<string, ConfigStatusSnapshot>());
        return null;
    }

    /// <summary>Returns the current local-only snapshot, or null if not yet populated.</summary>
    public LoopStatusDto? Read() => Volatile.Read(ref _value);
}
