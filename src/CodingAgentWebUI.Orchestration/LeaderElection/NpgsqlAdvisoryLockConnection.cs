using System.Data;
using Npgsql;

namespace CodingAgentWebUI.Orchestration.LeaderElection;

/// <summary>
/// Production implementation of <see cref="IAdvisoryLockConnection"/> wrapping a real <see cref="NpgsqlConnection"/>.
/// </summary>
internal sealed class NpgsqlAdvisoryLockConnection : IAdvisoryLockConnection
{
    private readonly NpgsqlConnection _connection;

    public NpgsqlAdvisoryLockConnection(string connectionString)
    {
        _connection = new NpgsqlConnection(connectionString);
    }

    public ConnectionState State => _connection.State;

    public Task OpenAsync(CancellationToken ct) => _connection.OpenAsync(ct);

    public async Task<bool> TryAcquireLockAsync(long lockKey, CancellationToken ct)
    {
        await using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT pg_try_advisory_lock(@key)";
        cmd.Parameters.AddWithValue("key", lockKey);
        var result = await cmd.ExecuteScalarAsync(ct);
        return result is true;
    }

    // TODO: VerifyLockIsHeldAsync casts the lower 32 bits of the lock key via (@key & x'FFFFFFFF'::bigint)::int.
    // If a user configures a LockKey with lower 32 bits > 0x7FFFFFFF (e.g., 0xDEADBEEF), PostgreSQL will
    // raise "integer out of range". The default key (0x0CAA_1EAD) is safe, but the option is exposed.
    // Consider using pg_try_advisory_lock as the verify step, or splitting the key with signed-aware casts.
    public async Task<bool> VerifyLockIsHeldAsync(long lockKey, CancellationToken ct)
    {
        await using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            SELECT EXISTS(
                SELECT 1 FROM pg_locks
                WHERE locktype = 'advisory'
                  AND classid = (@key >> 32)::int
                  AND objid = (@key & x'FFFFFFFF'::bigint)::int
                  AND pid = pg_backend_pid()
                  AND granted = true
            )
            """;
        cmd.Parameters.AddWithValue("key", lockKey);
        var result = await cmd.ExecuteScalarAsync(ct);
        return result is true;
    }

    public async Task ReleaseLockAsync(long lockKey)
    {
        await using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT pg_advisory_unlock(@key)";
        cmd.Parameters.AddWithValue("key", lockKey);
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task CloseAsync()
    {
        await _connection.CloseAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await _connection.DisposeAsync();
    }

    public void Dispose()
    {
        _connection.Dispose();
    }
}

/// <summary>
/// Production implementation of <see cref="IAdvisoryLockConnectionFactory"/>
/// that creates <see cref="NpgsqlAdvisoryLockConnection"/> instances.
/// </summary>
internal sealed class NpgsqlAdvisoryLockConnectionFactory : IAdvisoryLockConnectionFactory
{
    private readonly string _connectionString;

    public NpgsqlAdvisoryLockConnectionFactory(string connectionString)
    {
        _connectionString = connectionString;
    }

    public IAdvisoryLockConnection Create() => new NpgsqlAdvisoryLockConnection(_connectionString);
}
