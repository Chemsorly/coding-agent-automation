using System.Data.Common;
using CodingAgentWebUI.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Serilog;

namespace CodingAgentWebUI.Infrastructure.Locking;

/// <summary>
/// PostgreSQL-backed distributed lock using pg_try_advisory_lock(hashtext(lockName)).
/// Uses a retry loop with bounded deadline instead of blocking pg_advisory_lock,
/// preventing indefinite hangs when a crashed holder's TCP session lingers.
/// Advisory locks are connection-scoped — a dedicated connection is kept alive until dispose.
/// </summary>
internal sealed class PostgresDistributedLockProvider : IDistributedLockProvider
{
    private static readonly ILogger Logger = Log.ForContext<PostgresDistributedLockProvider>();

    private readonly IDbContextFactory<PipelineDbContext> _factory;

    /// <summary>Maximum time to wait for lock acquisition before giving up.</summary>
    private static readonly TimeSpan AcquisitionTimeout = TimeSpan.FromSeconds(60);

    /// <summary>Delay between retry attempts.</summary>
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(2);

    public PostgresDistributedLockProvider(IDbContextFactory<PipelineDbContext> factory)
    {
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));
    }

    public async Task<IAsyncDisposable> AcquireAsync(string lockName, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(lockName);

        var db = await _factory.CreateDbContextAsync(ct);
        var conn = db.Database.GetDbConnection();
        await conn.OpenAsync(ct);

        var deadline = DateTimeOffset.UtcNow + AcquisitionTimeout;
        var attempt = 0;

        while (true)
        {
            ct.ThrowIfCancellationRequested();

            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT pg_try_advisory_lock(hashtext(@lockName))";
            var param = cmd.CreateParameter();
            param.ParameterName = "lockName";
            param.Value = lockName;
            cmd.Parameters.Add(param);

            var result = await cmd.ExecuteScalarAsync(ct);
            var acquired = result is true;

            if (acquired)
            {
                Logger.Debug("Advisory lock '{LockName}' acquired on attempt {Attempt}", lockName, attempt + 1);
                return new AdvisoryLockHandle(conn, lockName, db);
            }

            attempt++;
            if (DateTimeOffset.UtcNow >= deadline)
            {
                await conn.CloseAsync();
                await db.DisposeAsync();
                throw new TimeoutException(
                    $"Failed to acquire distributed lock '{lockName}' within {AcquisitionTimeout.TotalSeconds}s " +
                    $"after {attempt} attempts. Another holder may be stuck.");
            }

            Logger.Debug("Advisory lock '{LockName}' not available, retrying in {Delay}s (attempt {Attempt})",
                lockName, RetryDelay.TotalSeconds, attempt);

            await Task.Delay(RetryDelay, ct);
        }
    }

    private sealed class AdvisoryLockHandle : IAsyncDisposable
    {
        private readonly DbConnection _connection;
        private readonly string _lockName;
        private readonly PipelineDbContext _dbContext;
        private bool _disposed;

        public AdvisoryLockHandle(DbConnection connection, string lockName, PipelineDbContext dbContext)
        {
            _connection = connection;
            _lockName = lockName;
            _dbContext = dbContext;
        }

        public async ValueTask DisposeAsync()
        {
            if (_disposed) return;
            _disposed = true;

            try
            {
                await using var cmd = _connection.CreateCommand();
                cmd.CommandText = "SELECT pg_advisory_unlock(hashtext(@lockName))";
                var param = cmd.CreateParameter();
                param.ParameterName = "lockName";
                param.Value = _lockName;
                cmd.Parameters.Add(param);
                await cmd.ExecuteNonQueryAsync();
            }
            catch (Exception ex)
            {
                Logger.Warning(ex, "Failed to explicitly release advisory lock '{LockName}' — connection close will release it", _lockName);
            }
            finally
            {
                await _connection.CloseAsync();
                await _dbContext.DisposeAsync();
            }
        }
    }
}
