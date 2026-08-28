using CodingAgentWebUI.Api.Client;
using CodingAgentWebUI.Orchestration.Redis;
using CodingAgentWebUI.Pipeline;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Pipeline.Services;
using System.Text.Json;
using ILogger = Serilog.ILogger;
using ILeaderGate = CodingAgentWebUI.Pipeline.Interfaces.ILeaderGate;

namespace CodingAgentWebUI.Scheduler;

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
            .GetValue<string>("AGENT_API_KEY")
            ?? app.ServiceProvider.GetRequiredService<IConfiguration>()
                .GetValue<string>("AgentApiKey");

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

    // TODO [WARNING]: RedisTtl (30s) may be shorter than the configured poll interval (up to 300s).
    // During the idle DelayOrStop wait between cycles, no OnChange fires, so the Redis key can
    // expire before the next cycle update. Non-leader pods would then fall back to BuildDto(loopService)
    // and serve stale local state for the remainder of the poll window. Consider increasing this TTL
    // to exceed the maximum configured poll interval, or having the leader periodically refresh the key.
    private static readonly TimeSpan RedisTtl = TimeSpan.FromSeconds(30);

    private readonly IRedisStore? _store;
    private readonly ILeaderGate? _leaderGate;
    private readonly ILogger _logger;
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
    /// </summary>
    public void Update(LoopStatusDto dto)
    {
        Volatile.Write(ref _value, dto);

        if (_store is null) return;

        // TODO [WARNING]: Non-leader pods can overwrite the leader's correct Redis snapshot.
        // Update() has no _leaderGate.IsLeader guard on the write path. On non-leader pods,
        // AutoStartSchedulerLoopAsync calls StartLoopAsync at boot, which fires OnChange and
        // invokes Update() with the stale "🔄 Loop starting…" DTO — overwriting the leader's
        // last-known-good Redis value for up to one leader OnChange interval.
        // Fix: guard the Redis write with: if (_leaderGate is not null && !_leaderGate.IsLeader) return;

        // Fire-and-forget: OnChange is event Action? (synchronous), so we cannot await.
        // ContinueWith(OnlyOnFaulted) logs any Redis failure without blocking the caller.
        // TODO [WARNING]: Pass TaskScheduler.Default as the third overload argument to ContinueWith
        // to avoid relying on TaskScheduler.Current, which is implementation-dependent in ASP.NET Core.
        // Safe form: .ContinueWith(t => ..., CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default)
        _ = _store.SetAsync(RedisKey, JsonSerializer.Serialize(dto, PipelineJsonOptions.Default), RedisTtl)
            .ContinueWith(
                t => _logger.Warning(t.Exception, "LoopStatusCache: Redis write failed for key {Key}", RedisKey),
                TaskContinuationOptions.OnlyOnFaulted);
    }

    /// <summary>
    /// Returns the current snapshot, checking local memory first when this pod is the leader
    /// (fast path — no Redis round-trip), then falling back to the Redis snapshot written by
    /// the leader pod, then returning null.
    /// <para>
    /// When <see cref="ILeaderGate"/> is provided and <see cref="ILeaderGate.IsLeader"/> is
    /// false, the local fast-path is skipped so that non-leader pods always serve the Redis
    /// snapshot rather than their own stale initial state.
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
        // TODO [WARNING]: A newly-elected leader pod will serve this Redis path (potentially stale,
        // from the prior leader) rather than its own up-to-date local _value until it fires the
        // next OnChange and writes a fresh snapshot. The window is bounded by the next cycle, but
        // it is worth noting that a leadership change mid-run can briefly surface a prior leader's
        // snapshot to status requests on the newly-elected pod.
        if (_store is null) return null;
        try
        {
            var json = await _store.GetAsync(RedisKey);
            if (json is null) return null;
            return JsonSerializer.Deserialize<LoopStatusDto>(json, PipelineJsonOptions.Lenient);
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "LoopStatusCache: Redis read failed for key {Key} — falling back to null", RedisKey);
            return null;
        }
    }

    /// <summary>Returns the current local-only snapshot, or null if not yet populated.</summary>
    public LoopStatusDto? Read() => Volatile.Read(ref _value);
}
