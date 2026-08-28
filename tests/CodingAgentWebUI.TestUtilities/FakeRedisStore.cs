using System.Collections.Concurrent;
using System.Text.Json;
using CodingAgentWebUI.Orchestration.Redis;
using StackExchange.Redis;

namespace CodingAgentWebUI.TestUtilities;

/// <summary>
/// In-memory implementation of <see cref="IRedisStore"/> for unit tests.
/// Not thread-safe for concurrent writers — sufficient for sequential unit test scenarios.
/// For concurrent integration tests use a real Redis container (TestContainers).
/// </summary>
public sealed class FakeRedisStore : IRedisStore
{
    // ── String keys ──────────────────────────────────────────────────
    private readonly ConcurrentDictionary<string, (string Value, DateTimeOffset? Expiry)> _strings = new();

    // ── Hash keys ────────────────────────────────────────────────────
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, string>> _hashes = new();

    // ── Set keys ─────────────────────────────────────────────────────
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, bool>> _sets = new();

    // ── List keys ────────────────────────────────────────────────────
    private readonly ConcurrentDictionary<string, List<string>> _lists = new();

    // ── TTL tracking ─────────────────────────────────────────────────
    private readonly ConcurrentDictionary<string, DateTimeOffset> _expiries = new();

    // For tracking the last Lua EXPIREAT value per key (for test assertions)
    public readonly ConcurrentDictionary<string, DateTimeOffset> ExpireAtCalls = new();

    // ── String operations ─────────────────────────────────────────────

    public Task<bool> SetAsync(string key, string value, TimeSpan? expiry = null, When when = When.Always)
    {
        var expires = expiry.HasValue ? DateTimeOffset.UtcNow + expiry.Value : (DateTimeOffset?)null;

        if (when == When.NotExists)
        {
            if (_strings.ContainsKey(key) && !IsExpired(key))
                return Task.FromResult(false);
        }

        _strings[key] = (value, expires);
        if (expires.HasValue) _expiries[key] = expires.Value;
        return Task.FromResult(true);
    }

    public Task<bool> SetIfNotExistsAsync(string key, string value, TimeSpan expiry)
    {
        if (_strings.ContainsKey(key) && !IsExpired(key))
            return Task.FromResult(false);

        var expires = DateTimeOffset.UtcNow + expiry;
        _strings[key] = (value, expires);
        _expiries[key] = expires;
        return Task.FromResult(true);
    }

    public Task<bool> DeleteAsync(string key)
    {
        var existed = _strings.TryRemove(key, out _)
                   || _hashes.TryRemove(key, out _)
                   || _sets.TryRemove(key, out _)
                   || _lists.TryRemove(key, out _);
        _expiries.TryRemove(key, out _);
        return Task.FromResult(existed);
    }

    public Task<bool> ExpireAsync(string key, TimeSpan expiry)
    {
        var at = DateTimeOffset.UtcNow + expiry;
        _expiries[key] = at;
        return Task.FromResult(true);
    }

    public Task<bool> ExpireAtAsync(string key, DateTimeOffset expiry)
    {
        _expiries[key] = expiry;
        ExpireAtCalls[key] = expiry;
        return Task.FromResult(true);
    }

    // ── Hash operations ───────────────────────────────────────────────

    public Task<HashEntry[]> HashGetAllAsync(string key)
    {
        if (IsExpired(key) || !_hashes.TryGetValue(key, out var hash))
            return Task.FromResult(Array.Empty<HashEntry>());

        return Task.FromResult(hash.Select(kv => new HashEntry(kv.Key, kv.Value)).ToArray());
    }

    public Task HashSetAsync(string key, HashEntry[] fields)
    {
        // Clear any stale expiry — real Redis: HSET on a key with a past TTL resurrects it with no TTL.
        // Without this, a HashSetAsync after ExpireAtAsync(past) leaves the stale expiry in place;
        // the next HashGetAllAsync hits IsExpired → lazy-removes the hash → returns [] despite the write.
        _expiries.TryRemove(key, out _);
        var hash = _hashes.GetOrAdd(key, _ => new ConcurrentDictionary<string, string>());
        foreach (var entry in fields)
            hash[(string)entry.Name!] = (string)entry.Value!;
        return Task.CompletedTask;
    }

    public Task<bool> HashSetFieldAsync(string key, string field, string value)
    {
        // Same as HashSetAsync: clear stale expiry on write to match real Redis semantics.
        _expiries.TryRemove(key, out _);
        var hash = _hashes.GetOrAdd(key, _ => new ConcurrentDictionary<string, string>());
        var isNew = !hash.ContainsKey(field);
        hash[field] = value;
        return Task.FromResult(isNew);
    }

    // ── Set operations ────────────────────────────────────────────────

    public Task<long> SetAddAsync(string key, string value)
    {
        var set = _sets.GetOrAdd(key, _ => new ConcurrentDictionary<string, bool>());
        return Task.FromResult(set.TryAdd(value, true) ? 1L : 0L);
    }

    public Task<long> SetRemoveAsync(string key, string value)
    {
        if (!_sets.TryGetValue(key, out var set))
            return Task.FromResult(0L);
        return Task.FromResult(set.TryRemove(value, out _) ? 1L : 0L);
    }

    public Task<string[]> SetMembersAsync(string key)
    {
        if (IsExpired(key) || !_sets.TryGetValue(key, out var set))
            return Task.FromResult(Array.Empty<string>());
        return Task.FromResult(set.Keys.ToArray());
    }

    public Task<long> SetCardinalityAsync(string key)
    {
        if (IsExpired(key) || !_sets.TryGetValue(key, out var set))
            return Task.FromResult(0L);
        return Task.FromResult((long)set.Count);
    }

    // ── List operations ───────────────────────────────────────────────

    public Task<long> ListRightPushAsync(string key, string[] values)
    {
        var list = _lists.GetOrAdd(key, _ => []);
        lock (list) { list.AddRange(values); }
        return Task.FromResult((long)list.Count);
    }

    public Task ListTrimAsync(string key, long start, long stop)
    {
        if (!_lists.TryGetValue(key, out var list)) return Task.CompletedTask;
        lock (list)
        {
            var count = list.Count;
            // Normalize negative indices
            var s = start < 0 ? Math.Max(0, count + (int)start) : (int)start;
            var e = stop < 0 ? count + (int)stop : (int)stop;
            if (s > e || s >= count) { list.Clear(); return Task.CompletedTask; }
            e = Math.Min(e, count - 1);
            var kept = list.Skip(s).Take(e - s + 1).ToList();
            list.Clear();
            list.AddRange(kept);
        }
        return Task.CompletedTask;
    }

    public Task<string[]> ListRangeAsync(string key, long start, long stop)
    {
        if (IsExpired(key) || !_lists.TryGetValue(key, out var list))
            return Task.FromResult(Array.Empty<string>());
        lock (list)
        {
            var count = list.Count;
            var s = start < 0 ? Math.Max(0, count + (int)start) : (int)start;
            var e = stop < 0 ? count + (int)stop : (int)stop;
            if (s >= count || s > e) return Task.FromResult(Array.Empty<string>());
            e = Math.Min(e, count - 1);
            return Task.FromResult(list.Skip(s).Take(e - s + 1).ToArray());
        }
    }

    // ── Existence / ping ──────────────────────────────────────────────

    public Task<bool> ExistsAsync(string key)
    {
        if (IsExpired(key)) return Task.FromResult(false);
        var exists = _strings.ContainsKey(key) || _hashes.ContainsKey(key)
                  || _sets.ContainsKey(key) || _lists.ContainsKey(key);
        return Task.FromResult(exists);
    }

    public Task<string?> GetAsync(string key)
    {
        if (IsExpired(key) || !_strings.TryGetValue(key, out var entry))
            return Task.FromResult<string?>(null);
        return Task.FromResult<string?>(entry.Value);
    }

    public Task<bool> PingAsync() => Task.FromResult(true);

    // ── Lua script ────────────────────────────────────────────────────

    /// <summary>
    /// Simulates the RemoveRun Lua script: SREM runs:active + EXPIREAT all run keys.
    /// The real Lua script operates on KEYS[1]=runs:active set, KEYS[2]=run:{id},
    /// KEYS[3]=run:{id}:output, KEYS[4]=run:{id}:chat, KEYS[5]=run:{id}:qg, KEYS[6]=run:{id}:retryerrors
    /// ARGV[1]=runId, ARGV[2]=unix expiry timestamp.
    /// Returns the hash entries of the run if SREM succeeded, or null otherwise.
    /// </summary>
    public Task<RedisResult> ScriptEvaluateAsync(string script, RedisKey[] keys, RedisValue[] values)
    {
        // Identify RemoveRun script by presence of SREM + EXPIREAT pattern
        if (script.Contains("SREM") && script.Contains("EXPIREAT") && keys.Length >= 2)
        {
            var setKey = (string)keys[0]!;
            var runId = (string)values[0]!;
            var expiryUnix = (long)values[1];
            var expiry = DateTimeOffset.FromUnixTimeSeconds(expiryUnix);

            // SREM
            if (!_sets.TryGetValue(setKey, out var set) || !set.TryRemove(runId, out _))
                return Task.FromResult(RedisResult.Create(RedisValue.Null));

            // EXPIREAT all run keys
            for (var i = 1; i < keys.Length; i++)
            {
                var rk = (string)keys[i]!;
                _expiries[rk] = expiry;
                ExpireAtCalls[rk] = expiry;
            }

            // Return HGETALL of run hash
            var hashKey = (string)keys[1]!;
            if (!_hashes.TryGetValue(hashKey, out var hash))
                return Task.FromResult(RedisResult.Create(Array.Empty<RedisValue>()));

            var entries = hash.SelectMany(kv => new[] { (RedisValue)kv.Key, (RedisValue)kv.Value }).ToArray();
            return Task.FromResult(RedisResult.Create(entries.Select(e => RedisResult.Create(e)).ToArray()));
        }

        // Default: return null for unknown scripts
        return Task.FromResult(RedisResult.Create(RedisValue.Null));
    }

    // ── Test helpers ──────────────────────────────────────────────────

    /// <summary>Directly read a hash (for test assertions).</summary>
    public IReadOnlyDictionary<string, string>? GetHash(string key)
        => _hashes.TryGetValue(key, out var h) ? h : null;

    /// <summary>Directly read a set (for test assertions).</summary>
    public IReadOnlyCollection<string> GetSet(string key)
        => _sets.TryGetValue(key, out var s) ? (IReadOnlyCollection<string>)s.Keys.ToList() : Array.Empty<string>();

    /// <summary>Directly read a list (for test assertions).</summary>
    public IReadOnlyList<string> GetList(string key)
    {
        if (!_lists.TryGetValue(key, out var l)) return Array.Empty<string>();
        lock (l) return l.ToList().AsReadOnly();
    }

    /// <summary>Simulate TTL expiry of a key (for test scenarios).</summary>
    public void ForceExpire(string key)
    {
        _strings.TryRemove(key, out _);
        _hashes.TryRemove(key, out _);
        _sets.TryRemove(key, out _);
        _lists.TryRemove(key, out _);
        _expiries.TryRemove(key, out _);
    }

    /// <summary>
    /// Returns the expiry timestamp set for a key via <see cref="ExpireAsync"/> or
    /// <see cref="ExpireAtAsync"/>, or <c>null</c> if no expiry is set.
    /// Use this in tests to assert that TTL was refreshed by a write operation.
    /// </summary>
    public DateTimeOffset? GetExpiry(string key)
        => _expiries.TryGetValue(key, out var expiry) ? expiry : null;

    /// <summary>
    /// Wipes all state. Use in <c>IAsyncLifetime.InitializeAsync</c> of fixtures that share this
    /// store across tests (e.g. multi-replica fixtures).
    /// </summary>
    public void Reset()
    {
        _strings.Clear();
        _hashes.Clear();
        _sets.Clear();
        _lists.Clear();
        _expiries.Clear();
        ExpireAtCalls.Clear();
    }

    // ── Private helpers ───────────────────────────────────────────────

    private bool IsExpired(string key)
    {
        if (!_expiries.TryGetValue(key, out var expiry)) return false;
        if (DateTimeOffset.UtcNow <= expiry) return false;
        // Lazily remove expired key
        _strings.TryRemove(key, out _);
        _hashes.TryRemove(key, out _);
        _sets.TryRemove(key, out _);
        _lists.TryRemove(key, out _);
        _expiries.TryRemove(key, out _);
        return true;
    }
}
