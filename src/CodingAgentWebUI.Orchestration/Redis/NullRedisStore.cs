using StackExchange.Redis;

namespace CodingAgentWebUI.Orchestration.Redis;

/// <summary>
/// No-op <see cref="IRedisStore"/> used when Redis is not configured.
/// All operations are safe no-ops or return empty values.
/// Used as a fallback so cleanup background services can still be registered
/// without Redis, while doing nothing useful at runtime.
/// </summary>
internal sealed class NullRedisStore : IRedisStore
{
    public Task<bool> SetAsync(string key, string value, TimeSpan? expiry = null, When when = When.Always) => Task.FromResult(false);
    public Task<bool> SetIfNotExistsAsync(string key, string value, TimeSpan expiry) => Task.FromResult(false);
    public Task<bool> DeleteAsync(string key) => Task.FromResult(false);
    public Task<bool> ExpireAsync(string key, TimeSpan expiry) => Task.FromResult(false);
    public Task<bool> ExpireAtAsync(string key, DateTimeOffset expiry) => Task.FromResult(false);
    public Task<HashEntry[]> HashGetAllAsync(string key) => Task.FromResult(Array.Empty<HashEntry>());
    public Task HashSetAsync(string key, HashEntry[] fields) => Task.CompletedTask;
    public Task<bool> HashSetFieldAsync(string key, string field, string value) => Task.FromResult(false);
    public Task<long> SetAddAsync(string key, string value) => Task.FromResult(0L);
    public Task<long> SetRemoveAsync(string key, string value) => Task.FromResult(0L);
    public Task<string[]> SetMembersAsync(string key) => Task.FromResult(Array.Empty<string>());
    public Task<long> SetCardinalityAsync(string key) => Task.FromResult(0L);
    public Task<long> ListRightPushAsync(string key, string[] values) => Task.FromResult(0L);
    public Task ListTrimAsync(string key, long start, long stop) => Task.CompletedTask;
    public Task<string[]> ListRangeAsync(string key, long start, long stop) => Task.FromResult(Array.Empty<string>());
    public Task<bool> ExistsAsync(string key) => Task.FromResult(false);
    public Task<bool> PingAsync() => Task.FromResult(false);
    public Task<RedisResult> ScriptEvaluateAsync(string script, RedisKey[] keys, RedisValue[] values)
        => Task.FromResult(RedisResult.Create(RedisValue.Null));
}
