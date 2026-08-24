using StackExchange.Redis;

namespace CodingAgentWebUI.Orchestration.Redis;

/// <summary>
/// Production implementation of <see cref="IRedisStore"/> backed by StackExchange.Redis <see cref="IDatabase"/>.
/// </summary>
public sealed class RedisStore : IRedisStore
{
    private readonly IDatabase _db;

    public RedisStore(IDatabase db)
    {
        ArgumentNullException.ThrowIfNull(db);
        _db = db;
    }

    public async Task<bool> SetAsync(string key, string value, TimeSpan? expiry = null, When when = When.Always)
        => await _db.StringSetAsync(key, value, expiry, when: when);

    public async Task<bool> SetIfNotExistsAsync(string key, string value, TimeSpan expiry)
        => await _db.StringSetAsync(key, value, expiry, when: When.NotExists);

    public async Task<bool> DeleteAsync(string key)
        => await _db.KeyDeleteAsync(key);

    public async Task<bool> ExpireAsync(string key, TimeSpan expiry)
        => await _db.KeyExpireAsync(key, expiry);

    public async Task<bool> ExpireAtAsync(string key, DateTimeOffset expiry)
        => await _db.KeyExpireAsync(key, expiry.UtcDateTime);

    public async Task<HashEntry[]> HashGetAllAsync(string key)
        => await _db.HashGetAllAsync(key);

    public async Task HashSetAsync(string key, HashEntry[] fields)
        => await _db.HashSetAsync(key, fields);

    public async Task<bool> HashSetFieldAsync(string key, string field, string value)
        => await _db.HashSetAsync(key, field, value);

    public async Task<long> SetAddAsync(string key, string value)
        => await _db.SetAddAsync(key, value) ? 1L : 0L;

    public async Task<long> SetRemoveAsync(string key, string value)
        => await _db.SetRemoveAsync(key, value) ? 1L : 0L;

    public async Task<string[]> SetMembersAsync(string key)
    {
        var members = await _db.SetMembersAsync(key);
        return members.Select(m => (string)m!).Where(m => m is not null).ToArray();
    }

    public async Task<long> SetCardinalityAsync(string key)
        => await _db.SetLengthAsync(key);

    public async Task<long> ListRightPushAsync(string key, string[] values)
        => await _db.ListRightPushAsync(key, values.Select(v => (RedisValue)v).ToArray());

    public async Task ListTrimAsync(string key, long start, long stop)
        => await _db.ListTrimAsync(key, start, stop);

    public async Task<string[]> ListRangeAsync(string key, long start, long stop)
    {
        var items = await _db.ListRangeAsync(key, start, stop);
        return items.Select(i => (string)i!).Where(i => i is not null).ToArray();
    }

    public async Task<bool> ExistsAsync(string key)
        => await _db.KeyExistsAsync(key);

    public async Task<bool> PingAsync()
    {
        var result = await _db.PingAsync();
        return result != TimeSpan.Zero;
    }

    public async Task<RedisResult> ScriptEvaluateAsync(string script, RedisKey[] keys, RedisValue[] values)
        => await _db.ScriptEvaluateAsync(script, keys, values);
}
