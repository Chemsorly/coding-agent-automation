using System.Collections.Concurrent;
using System.Text.Json;
using CodingAgentWebUI.Api.Client;
using CodingAgentWebUI.Orchestration.Redis;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Pipeline.Services;
using StackExchange.Redis;
using ILogger = Serilog.ILogger;

namespace CodingAgentWebUI.Orchestration;

/// <summary>
/// Redis-backed implementation of <see cref="IOrchestratorRunService"/>.
/// Replaces <see cref="OrchestratorRunService"/> when <c>IConnectionMultiplexer</c> is available,
/// enabling <c>api.replicas > 1</c>.
///
/// <para>
/// Key schema:
/// <list type="bullet">
///   <item><c>run:{runId}</c> — Hash of all scalar/complex <see cref="PipelineRun"/> fields.</item>
///   <item><c>runs:active</c> — Set of active runId strings.</item>
///   <item><c>run:{runId}:output</c> — List (output ring buffer, capped at 500).</item>
///   <item><c>run:{runId}:chat</c> — List (chat history, capped at 200).</item>
///   <item><c>run:{runId}:qg</c> — List (quality gate reports as JSON, capped at 20).</item>
///   <item><c>run:{runId}:retryerrors</c> — List (retry error messages, capped at 50).</item>
///   <item><c>recently-completed:{configId}:{issueId}</c> — String with 120s TTL.</item>
/// </list>
/// </para>
///
/// <para>
/// <b>Sync/async bridging:</b> <see cref="IOrchestratorRunService"/> is a synchronous interface.
/// All methods call async Redis operations via <c>.GetAwaiter().GetResult()</c>.
/// This is safe because all callers run on the ThreadPool (SignalR hub methods, hosted services) —
/// no synchronization context is captured, eliminating deadlock risk.
/// </para>
/// </summary>
public sealed class DistributedRunService : IOrchestratorRunService
{
    private static readonly TimeSpan RunPostCompletionTtl = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan RecentlyCompletedTtl = TimeSpan.FromSeconds(120);

    private readonly IRedisStore _store;
    private readonly IPipelineApiWorkItemClient _workItemClient;
    private readonly ILogger _logger;

    // Lua script: atomically SREM + EXPIREAT all run keys in one round-trip.
    // KEYS: [1]=runs:active set, [2]=run:{id}, [3]=run:{id}:output, [4]=run:{id}:chat,
    //       [5]=run:{id}:qg, [6]=run:{id}:retryerrors
    // ARGV: [1]=runId, [2]=unix expiry timestamp (seconds)
    // Returns: HGETALL of run:{id} as a flat array, or nil if SREM returned 0.
    private const string RemoveRunScript = @"
local removed = redis.call('SREM', KEYS[1], ARGV[1])
if removed == 0 then return nil end
local hash = redis.call('HGETALL', KEYS[2])
redis.call('EXPIREAT', KEYS[2], ARGV[2])
redis.call('EXPIREAT', KEYS[3], ARGV[2])
redis.call('EXPIREAT', KEYS[4], ARGV[2])
redis.call('EXPIREAT', KEYS[5], ARGV[2])
redis.call('EXPIREAT', KEYS[6], ARGV[2])
return hash
";

    public DistributedRunService(IRedisStore store, IPipelineApiWorkItemClient workItemClient, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(workItemClient);
        ArgumentNullException.ThrowIfNull(logger);
        _store = store;
        _workItemClient = workItemClient;
        _logger = logger;
    }

    // ── Keys ──────────────────────────────────────────────────────────

    private static string RunKey(string runId) => $"run:{runId}";
    private static string OutputKey(string runId) => $"run:{runId}:output";
    private static string ChatKey(string runId) => $"run:{runId}:chat";
    private static string QgKey(string runId) => $"run:{runId}:qg";
    private static string RetryErrorsKey(string runId) => $"run:{runId}:retryerrors";
    private const string ActiveSetKey = "runs:active";

    // ── HasActiveRuns ─────────────────────────────────────────────────

    /// <inheritdoc />
    public bool HasActiveRuns
        => _store.SetCardinalityAsync(ActiveSetKey).GetAwaiter().GetResult() > 0; // Safe: ThreadPool

    /// <inheritdoc />
    public int ActiveRunCount
        => (int)_store.SetCardinalityAsync(ActiveSetKey).GetAwaiter().GetResult(); // Safe: ThreadPool

    // ── AddRun ────────────────────────────────────────────────────────

    /// <inheritdoc />
    public void AddRun(PipelineRun run)
    {
        ArgumentNullException.ThrowIfNull(run);
        AddRunAsync(run).GetAwaiter().GetResult(); // Safe: ThreadPool
    }

    private async Task AddRunAsync(PipelineRun run)
    {
        await _store.HashSetAsync(RunKey(run.RunId), run.ToHashEntries());
        await _store.SetAddAsync(ActiveSetKey, run.RunId);
        _logger.Information("Active run added: {RunId} for issue {IssueIdentifier} (agent={AgentId})",
            run.RunId, run.IssueIdentifier, run.AgentId ?? "local");
    }

    // ── RemoveRun ─────────────────────────────────────────────────────

    /// <inheritdoc />
    public PipelineRun? RemoveRun(RunId runId)
    {
        ArgumentException.ThrowIfNullOrEmpty(runId.Value);
        return RemoveRunAsync(runId.Value).GetAwaiter().GetResult(); // Safe: ThreadPool
    }

    private async Task<PipelineRun?> RemoveRunAsync(string runId)
    {
        var expiryUnix = DateTimeOffset.UtcNow.Add(RunPostCompletionTtl).ToUnixTimeSeconds();

        var result = await _store.ScriptEvaluateAsync(
            RemoveRunScript,
            keys:
            [
                (RedisKey)ActiveSetKey,
                (RedisKey)RunKey(runId),
                (RedisKey)OutputKey(runId),
                (RedisKey)ChatKey(runId),
                (RedisKey)QgKey(runId),
                (RedisKey)RetryErrorsKey(runId)
            ],
            values: [(RedisValue)runId, (RedisValue)expiryUnix]);

        if (result.IsNull)
        {
            _logger.Debug("RemoveRun: run {RunId} not found in runs:active (already claimed or never added)", runId);
            return null;
        }

        // Reconstruct run from hash values returned by Lua
        var entries = (RedisResult[])result!;
        var hashEntries = new HashEntry[entries.Length / 2];
        for (var i = 0; i < entries.Length - 1; i += 2)
            hashEntries[i / 2] = new HashEntry((string)entries[i]!, (string)entries[i + 1]!);

        var run = PipelineRunHashExtensions.FromHash(hashEntries);
        if (run is null)
        {
            _logger.Warning("RemoveRun: run {RunId} claimed but hash could not be deserialized", runId);
            return null;
        }

        // Hydrate queue fields from Redis Lists before returning.
        // These are needed by AddRunToHistoryAsync in RunLifecycleManager for complete Postgres persistence.
        var outputLines = await _store.ListRangeAsync(OutputKey(runId), 0, -1);
        foreach (var line in outputLines) run.OutputLines.Enqueue(line);

        var chatEntries = await _store.ListRangeAsync(ChatKey(runId), 0, -1);
        foreach (var entry in chatEntries)
        {
            try
            {
                var chatEntry = JsonSerializer.Deserialize<ChatEntry>(entry);
                if (chatEntry is not null) run.ChatHistory.Enqueue(chatEntry);
            }
            catch { /* malformed entry — skip */ }
        }

        var qgReports = await _store.ListRangeAsync(QgKey(runId), 0, -1);
        foreach (var report in qgReports)
        {
            try
            {
                var qgReport = JsonSerializer.Deserialize<QualityGateReport>(report);
                if (qgReport is not null) run.QualityGateHistory.Enqueue(qgReport);
            }
            catch { /* malformed entry — skip */ }
        }

        var retryErrors = await _store.ListRangeAsync(RetryErrorsKey(runId), 0, -1);
        foreach (var error in retryErrors) run.RetryErrors.Enqueue(error);

        _logger.Information("Active run removed: {RunId}", runId);
        return run;
    }

    // ── GetRun ────────────────────────────────────────────────────────

    /// <inheritdoc />
    public PipelineRun? GetRun(RunId runId)
    {
        ArgumentException.ThrowIfNullOrEmpty(runId.Value);
        var hash = _store.HashGetAllAsync(RunKey(runId.Value)).GetAwaiter().GetResult(); // Safe: ThreadPool
        return hash.Length == 0 ? null : PipelineRunHashExtensions.FromHash(hash);
    }

    // ── GetActiveRuns ─────────────────────────────────────────────────

    /// <inheritdoc />
    public IReadOnlyList<PipelineRun> GetActiveRuns()
        => GetActiveRunsAsync().GetAwaiter().GetResult(); // Safe: ThreadPool

    private async Task<IReadOnlyList<PipelineRun>> GetActiveRunsAsync()
    {
        var members = await _store.SetMembersAsync(ActiveSetKey);
        var result = new List<PipelineRun>(members.Length);

        foreach (var runId in members)
        {
            var hash = await _store.HashGetAllAsync(RunKey(runId));
            if (hash.Length == 0) continue; // expired
            var run = PipelineRunHashExtensions.FromHash(hash);
            if (run is not null) result.Add(run);
        }

        return result.AsReadOnly();
    }

    // ── ReplaceRun ────────────────────────────────────────────────────

    /// <inheritdoc />
    public void ReplaceRun(PipelineRun run)
    {
        ArgumentNullException.ThrowIfNull(run);
        _store.HashSetAsync(RunKey(run.RunId), run.ToHashEntries()).GetAwaiter().GetResult(); // Safe: ThreadPool
        _logger.Debug("Active run replaced: {RunId} for issue {IssueIdentifier}", run.RunId, run.IssueIdentifier);
    }

    // ── AppendOutputLines ─────────────────────────────────────────────

    /// <inheritdoc />
    /// Distributed path: RPUSH to Redis List (bounded via LTRIM) + writes to in-memory buffer
    /// for same-request readers. The Redis List is the authoritative cross-replica source.
    public void AppendOutputLines(RunId runId, IReadOnlyList<string> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);
        if (lines.Count == 0) return;

        // Fire-and-forget Redis write — output streaming is best-effort
        _ = AppendOutputToRedisAsync(runId.Value, lines);
    }

    private async Task AppendOutputToRedisAsync(string runId, IReadOnlyList<string> lines)
    {
        await _store.ListRightPushAsync(OutputKey(runId), lines.ToArray());
        await _store.ListTrimAsync(OutputKey(runId), -500, -1); // Keep last 500
    }

    // ── GetOutputBuffer ───────────────────────────────────────────────

    /// <inheritdoc />
    /// Returns an <see cref="OutputRingBuffer"/> pre-populated from the Redis output list.
    /// Any lines already in Redis are loaded into the buffer so callers that read
    /// <see cref="OutputRingBuffer.GetAll()"/> — in particular <c>SubscribeToRun</c> backlog
    /// delivery — receive the full history rather than an empty buffer.
    public OutputRingBuffer GetOutputBuffer(RunId runId)
    {
        ArgumentException.ThrowIfNullOrEmpty(runId.Value);
        var buffer = new OutputRingBuffer();
        // Synchronously load existing lines from Redis (Safe: ThreadPool context only).
        var lines = _store.ListRangeAsync(OutputKey(runId.Value), 0, -1).GetAwaiter().GetResult();
        foreach (var line in lines)
            buffer.Add(line);
        return buffer;
    }

    /// <summary>
    /// Returns the full output backlog for a run from Redis (for SubscribeToRun cross-replica serving).
    /// </summary>
    public Task<string[]> GetOutputBacklogAsync(string runId)
        => _store.ListRangeAsync(OutputKey(runId), 0, -1);

    // ── IsIssueBeingProcessed ─────────────────────────────────────────

    /// <inheritdoc />
    /// Delegates to Postgres via <see cref="IPipelineApiWorkItemClient.IsIssueDistributedAsync"/>.
    /// Under multi-replica the in-memory scan is meaningless; the Postgres partial unique index
    /// is the authoritative source. See Spec 046 Req 4.12.
    public bool IsIssueBeingProcessed(IssueIdentifier issueIdentifier, ProviderConfigId issueProviderConfigId)
    {
        try
        {
            return _workItemClient.IsIssueDistributedAsync(
                issueIdentifier.Value,
                issueProviderConfigId.Value,
                CancellationToken.None).GetAwaiter().GetResult(); // Safe: ThreadPool
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "DistributedRunService.IsIssueBeingProcessed: API call failed — returning false (conservative)");
            return false;
        }
    }

    // ── Recently-completed anti-race ──────────────────────────────────

    /// <inheritdoc />
    public void MarkRecentlyCompleted(IssueIdentifier issueIdentifier, ProviderConfigId issueProviderConfigId)
    {
        ArgumentException.ThrowIfNullOrEmpty(issueIdentifier.Value, nameof(issueIdentifier));
        _ = _store.SetAsync(
            RecentlyCompletedKey(issueProviderConfigId.Value, issueIdentifier.Value),
            DateTimeOffset.UtcNow.ToString("O"),
            expiry: RecentlyCompletedTtl,
            when: When.Always);
    }

    /// <inheritdoc />
    public bool WasRecentlyCompleted(IssueIdentifier issueIdentifier, ProviderConfigId issueProviderConfigId)
    {
        ArgumentException.ThrowIfNullOrEmpty(issueIdentifier.Value, nameof(issueIdentifier));
        return _store.ExistsAsync(
            RecentlyCompletedKey(issueProviderConfigId.Value, issueIdentifier.Value))
            .GetAwaiter().GetResult(); // Safe: ThreadPool
    }

    // ── UpdateRunFieldsAsync ──────────────────────────────────────────

    /// <summary>
    /// Writes specific run fields directly to the Redis Hash (targeted HSET).
    /// Used by hub methods (<c>ReportStepTransition</c>, <c>ReportBrainSyncResult</c>, etc.)
    /// to avoid the full read-modify-write overhead of <see cref="ReplaceRun"/>.
    /// </summary>
    public async Task UpdateRunFieldsAsync(string runId, params HashEntry[] fields)
    {
        if (fields.Length == 0) return;
        await _store.HashSetAsync(RunKey(runId), fields);
    }

    // ── Private helpers ───────────────────────────────────────────────

    private static string RecentlyCompletedKey(string configId, string issueIdentifier)
        => $"recently-completed:{configId}:{issueIdentifier}";
}
