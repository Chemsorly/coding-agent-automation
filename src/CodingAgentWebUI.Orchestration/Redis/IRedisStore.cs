using StackExchange.Redis;

namespace CodingAgentWebUI.Orchestration.Redis;

/// <summary>
/// Thin abstraction over <see cref="IDatabase"/> from StackExchange.Redis.
/// Injected into all distributed stores to enable unit testing without a real Redis instance.
/// All methods are async to match the underlying StackExchange.Redis API.
/// </summary>
public interface IRedisStore
{
    /// <summary>SET key value [EX seconds] — returns true on success.</summary>
    Task<bool> SetAsync(string key, string value, TimeSpan? expiry = null, When when = When.Always);

    /// <summary>GET key — returns null if key does not exist or has expired.</summary>
    Task<string?> GetAsync(string key);

    /// <summary>SET key value NX PX ms — returns true if key was set (did not already exist).</summary>
    Task<bool> SetIfNotExistsAsync(string key, string value, TimeSpan expiry);

    /// <summary>DEL key — returns true if key existed.</summary>
    Task<bool> DeleteAsync(string key);

    /// <summary>EXPIRE key seconds.</summary>
    Task<bool> ExpireAsync(string key, TimeSpan expiry);

    /// <summary>EXPIREAT key unix-timestamp-seconds.</summary>
    Task<bool> ExpireAtAsync(string key, DateTimeOffset expiry);

    /// <summary>HGETALL key.</summary>
    Task<HashEntry[]> HashGetAllAsync(string key);

    /// <summary>HMSET key field value [field value ...].</summary>
    Task HashSetAsync(string key, HashEntry[] fields);

    /// <summary>HSET key field value — sets a single field. Returns true if the field was new.</summary>
    Task<bool> HashSetFieldAsync(string key, string field, string value);

    /// <summary>SADD key member — returns number of members added (0 if already present).</summary>
    Task<long> SetAddAsync(string key, string value);

    /// <summary>SREM key member — returns number of members removed (0 if not present).</summary>
    Task<long> SetRemoveAsync(string key, string value);

    /// <summary>SMEMBERS key — returns all members of the set.</summary>
    Task<string[]> SetMembersAsync(string key);

    /// <summary>SCARD key — returns set cardinality.</summary>
    Task<long> SetCardinalityAsync(string key);

    /// <summary>RPUSH key values — returns new list length.</summary>
    Task<long> ListRightPushAsync(string key, string[] values);

    /// <summary>LTRIM key start stop.</summary>
    Task ListTrimAsync(string key, long start, long stop);

    /// <summary>LRANGE key start stop — returns elements.</summary>
    Task<string[]> ListRangeAsync(string key, long start, long stop);

    /// <summary>EXISTS key — returns true if key exists.</summary>
    Task<bool> ExistsAsync(string key);

    /// <summary>PING — returns true if the server responded.</summary>
    Task<bool> PingAsync();

    /// <summary>
    /// Executes a Lua script atomically on the Redis server.
    /// Used for SREM + EXPIREAT atomicity in <c>RemoveRun</c>.
    /// </summary>
    Task<RedisResult> ScriptEvaluateAsync(string script, RedisKey[] keys, RedisValue[] values);
}
