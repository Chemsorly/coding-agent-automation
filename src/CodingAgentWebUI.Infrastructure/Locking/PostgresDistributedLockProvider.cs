using System.Data.Common;
using CodingAgentWebUI.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CodingAgentWebUI.Infrastructure.Locking;

/// <summary>
/// PostgreSQL-backed distributed lock using pg_advisory_lock(hashtext(lockName)).
/// Advisory locks are connection-scoped — a dedicated connection is kept alive until dispose.
/// </summary>
internal sealed class PostgresDistributedLockProvider : IDistributedLockProvider
{
    private readonly IDbContextFactory<PipelineDbContext> _factory;

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

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT pg_advisory_lock(hashtext(@lockName))";
        var param = cmd.CreateParameter();
        param.ParameterName = "lockName";
        param.Value = lockName;
        cmd.Parameters.Add(param);
        await cmd.ExecuteNonQueryAsync(ct);

        return new AdvisoryLockHandle(conn, lockName, db);
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
            finally
            {
                await _connection.CloseAsync();
                await _dbContext.DisposeAsync();
            }
        }
    }
}
