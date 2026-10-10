using System.Collections.Concurrent;
using System.Text.Json;
using CodingAgent.Orchestration.Redis;
using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;
using CodingAgent.Pipeline.Services;
using StackExchange.Redis;
using ILogger = Serilog.ILogger;

namespace CodingAgent.Orchestration;

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
    private readonly Func<string, string, CancellationToken, Task<bool>> _isIssueDistributedAsync;
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

    public DistributedRunService(IRedisStore store, Func<string, string, CancellationToken, Task<bool>> isIssueDistributedAsync, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(isIssueDistributedAsync);
        ArgumentNullException.ThrowIfNull(logger);
        _store = store;
        _isIssueDistributedAsync = isIssueDistributedAsync;
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

        // Reconstruct run from hash values returned by Lua.
        // Guard against empty or malformed HGETALL results (e.g. key expired between SREM and
        // HGETALL in a degraded Redis state, or corruption producing an odd-length array).
        // An empty result means the run was removed from the active set but its hash is gone —
        // log a warning and return null so the caller can handle the orphan rather than
        // silently dropping it with a partial/zero-initialized HashEntry array.
        var entries = (RedisResult[])result!;
        if (entries.Length == 0)
        {
            _logger.Warning("RemoveRun: run {RunId} removed from active set but HGETALL returned empty — hash may have already expired", runId);
            return null;
        }

        if (entries.Length % 2 != 0)
        {
            _logger.Warning("RemoveRun: run {RunId} HGETALL returned odd-length array ({Length}) — Redis data may be corrupted, skipping deserialization", runId, entries.Length);
            return null;
        }

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
        EnqueueDeserialized(chatEntries, run.ChatHistory);

        var qgReports = await _store.ListRangeAsync(QgKey(runId), 0, -1);
        EnqueueDeserialized(qgReports, run.QualityGateHistory);

        var retryErrors = await _store.ListRangeAsync(RetryErrorsKey(runId), 0, -1);
        foreach (var error in retryErrors) run.RetryErrors.Enqueue(error);

        _logger.Information("Active run removed: {RunId}", runId);
        return run;
    }

    private static void EnqueueDeserialized<T>(string[] entries, BoundedConcurrentQueue<T> target) where T : class
    {
        foreach (var entry in entries)
        {
            try
            {
                var item = JsonSerializer.Deserialize<T>(entry);
                if (item is not null) target.Enqueue(item);
            }
            catch { /* malformed entry — skip */ }
        }
    }

    // ── GetRun ────────────────────────────────────────────────────────

    /// <inheritdoc />
    public PipelineRun? GetRun(RunId runId)
    {
        ArgumentException.ThrowIfNullOrEmpty(runId.Value);
        var hash = _store.HashGetAllAsync(RunKey(runId.Value)).GetAwaiter().GetResult(); // Safe: ThreadPool
        if (hash.Length == 0)
        {
            _logger.Debug("GetRun: run {RunId} not found in Redis (key expired or never added)", runId.Value);
            return null;
        }
        return PipelineRunHashExtensions.FromHash(hash);
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
            if (hash.Length == 0)
            {
                _logger.Warning("GetActiveRuns: run {RunId} in active set but hash is empty (TTL expired) — active set may be stale", runId);
                continue;
            }
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
        ReplaceRunAsync(run).GetAwaiter().GetResult(); // Safe: ThreadPool
    }

    private async Task ReplaceRunAsync(PipelineRun run)
    {
        // TODO: TOCTOU race — SetMembersAsync (O(N) SMEMBERS scan) and the subsequent HashSetAsync
        // are not atomic. A concurrent RemoveRun (Lua SREM+EXPIREAT) can remove the RunId from the
        // active set between these two calls, causing HashSetAsync to write a hash for an inactive run
        // and potentially resurrect it without a TTL. Fixing this properly requires an atomic Lua
        // check-and-set script. Out of scope per issue #3450; leave for a follow-up.
        var members = await _store.SetMembersAsync(ActiveSetKey);
        if (!members.Contains(run.RunId))
        {
            _logger.Warning("ReplaceRun: run {RunId} is not in the active set — ignoring (run may have been removed)", run.RunId);
            return;
        }
        await _store.HashSetAsync(RunKey(run.RunId), run.ToHashEntries());
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
        _ = AppendOutputToRedisAsync(runId.Value, lines)
            .ContinueWith(t => _logger.Warning(t.Exception,
                "AppendOutputLines: Redis write failed for run {RunId} — output lines lost",
                runId.Value), TaskContinuationOptions.OnlyOnFaulted);
    }

    private async Task AppendOutputToRedisAsync(string runId, IReadOnlyList<string> lines)
    {
        await _store.ListRightPushAsync(OutputKey(runId), lines.ToArray());
        await _store.ListTrimAsync(OutputKey(runId), -500, -1); // Keep last 500
    }

    // ── GetOutputBacklogAsync ─────────────────────────────────────────

    /// <inheritdoc />
    /// Returns the full output backlog for a run from Redis (for SubscribeToRun cross-replica serving).
    public Task<IReadOnlyList<string>> GetOutputBacklogAsync(RunId runId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(runId.Value, nameof(runId));
        ct.ThrowIfCancellationRequested();
        return GetOutputBacklogInternalAsync(runId.Value, ct);
    }

    private async Task<IReadOnlyList<string>> GetOutputBacklogInternalAsync(string runId, CancellationToken ct)
    {
        // TODO: ct is not forwarded to _store.ListRangeAsync because IRedisStore.ListRangeAsync has
        // no CancellationToken overload. The entry-point guard (ct.ThrowIfCancellationRequested)
        // catches pre-cancelled tokens, but a cancellation that arrives after the Redis round-trip
        // starts cannot interrupt it. Add a ct overload to IRedisStore when available.
        var lines = await _store.ListRangeAsync(OutputKey(runId), 0, -1);
        return lines;
    }

    // ── AppendChatEntry / GetChatHistoryAsync ─────────────────────────

    /// <inheritdoc />
    /// Distributed path: RPUSH the JSON-serialized entry to run:{id}:chat, bounded via LTRIM to the last 200 entries.
    public void AppendChatEntry(RunId runId, ChatEntry entry)
    {
        ArgumentException.ThrowIfNullOrEmpty(runId.Value);
        ArgumentNullException.ThrowIfNull(entry);

        // Fire-and-forget Redis write — same delivery guarantee as AppendOutputLines.
        _ = AppendChatToRedisAsync(runId.Value, entry)
            .ContinueWith(t => _logger.Warning(t.Exception,
                "AppendChatEntry: Redis write failed for run {RunId} — chat entry lost",
                runId.Value), TaskContinuationOptions.OnlyOnFaulted);
    }

    private async Task AppendChatToRedisAsync(string runId, ChatEntry entry)
    {
        // Default JsonSerializer options: RemoveRunAsync reads with EnqueueDeserialized (same options).
        await _store.ListRightPushAsync(ChatKey(runId), [JsonSerializer.Serialize(entry)]);
        await _store.ListTrimAsync(ChatKey(runId), -PipelineConstants.DefaultChatHistoryCapacity, -1);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ChatEntry>> GetChatHistoryAsync(RunId runId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(runId.Value, nameof(runId));
        ct.ThrowIfCancellationRequested();
        // TODO: ct is not forwarded to _store.ListRangeAsync because IRedisStore.ListRangeAsync has no
        // CancellationToken overload. A cancellation that arrives after the Redis round-trip begins cannot
        // interrupt it. This is the same documented limitation as GetOutputBacklogAsync above. Fix when
        // IRedisStore gains cancellation support.
        var entries = await _store.ListRangeAsync(ChatKey(runId.Value), 0, -1);
        var history = new BoundedConcurrentQueue<ChatEntry>(PipelineConstants.DefaultChatHistoryCapacity);
        EnqueueDeserialized(entries, history);
        return history.ToArray();
    }

    // ── IsIssueBeingProcessed ─────────────────────────────────────────

    /// <inheritdoc />
    /// Delegates to Postgres via <see cref="IPipelineApiWorkItemClient.IsIssueDistributedAsync"/>.
    /// Under multi-replica the in-memory scan is meaningless; the Postgres partial unique index
    /// is the authoritative source. See Spec 046 Req 4.12.
    public bool IsIssueBeingProcessed(IssueIdentifier issueIdentifier, ProviderConfigId issueProviderConfigId)
    {
        ArgumentException.ThrowIfNullOrEmpty(issueIdentifier.Value, nameof(issueIdentifier));
        try
        {
            return _isIssueDistributedAsync(
                issueIdentifier.Value,
                issueProviderConfigId.Value,
                CancellationToken.None).GetAwaiter().GetResult(); // Safe: ThreadPool
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "DistributedRunService.IsIssueBeingProcessed: check failed — returning false (conservative)");
            return false;
        }
    }

    // ── GetActiveRunBranchesAsync ─────────────────────────────────────

    /// <inheritdoc />
    public async Task<HashSet<string>> GetActiveRunBranchesAsync(CancellationToken ct = default)
    {
        var runs = await GetActiveRunsAsync();
        return runs
            .Where(r => r.BranchName != null)
            .Select(r => r.BranchName!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    // ── Recently-completed anti-race ──────────────────────────────────

    /// <inheritdoc />
    public void MarkRecentlyCompleted(IssueIdentifier issueIdentifier, ProviderConfigId issueProviderConfigId)
    {
        ArgumentException.ThrowIfNullOrEmpty(issueIdentifier.Value, nameof(issueIdentifier)); // NOSONAR S3236 — names the parameter, not the .Value expression
        _ = _store.SetAsync(
            RecentlyCompletedKey(issueProviderConfigId.Value, issueIdentifier.Value),
            DateTimeOffset.UtcNow.ToString("O"),
            expiry: RecentlyCompletedTtl,
            when: When.Always)
            .ContinueWith(t => _logger.Warning(t.Exception,
                "MarkRecentlyCompleted: Redis write failed for issue {IssueIdentifier} — anti-race key not set, duplicate dispatch possible",
                issueIdentifier.Value), TaskContinuationOptions.OnlyOnFaulted);
    }

    /// <inheritdoc />
    public bool WasRecentlyCompleted(IssueIdentifier issueIdentifier, ProviderConfigId issueProviderConfigId)
    {
        ArgumentException.ThrowIfNullOrEmpty(issueIdentifier.Value, nameof(issueIdentifier)); // NOSONAR S3236 — names the parameter, not the .Value expression
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
