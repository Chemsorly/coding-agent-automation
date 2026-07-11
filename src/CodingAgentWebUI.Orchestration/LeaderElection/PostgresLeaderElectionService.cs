using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Npgsql;
using Serilog;

namespace CodingAgentWebUI.Orchestration.LeaderElection;

/// <summary>
/// Postgres advisory lock-based leader election for DB+SignalR multi-replica deployments
/// without Kubernetes. Uses a dedicated NpgsqlConnection (not from EF Core pool) to hold
/// a session-scoped advisory lock. Leadership is automatically lost when the connection drops
/// (Postgres advisory locks are session-scoped).
///
/// Satisfies the same contract as <see cref="LeaderElectionService"/> (K8s Lease):
/// <see cref="IsLeader"/>, <see cref="LeaderToken"/>, <see cref="OnStartedLeading"/>,
/// <see cref="OnStoppedLeading"/>.
/// </summary>
public sealed class PostgresLeaderElectionService : ILeaderElectionService, IHostedService, IDisposable
{
    private static readonly ILogger Log = Serilog.Log.ForContext<PostgresLeaderElectionService>();

    /// <summary>
    /// Well-known advisory lock key. Uses a distinctive constant to avoid collision
    /// with user advisory locks. 0xCAA_1EAD = "CAA LEAD(er)".
    /// </summary>
    internal const long LockKey = 0xCAA_1EAD;

    private readonly string _connectionString;
    private readonly PostgresLeaderElectionOptions _options;

    private NpgsqlConnection? _lockConnection;
    private CancellationTokenSource? _leaderCts;
    private CancellationTokenSource? _serviceCts;
    private Task? _electionTask;

    private volatile bool _isLeader;

    /// <inheritdoc />
    public bool IsLeader => _isLeader;

    /// <inheritdoc />
    // TODO: After LoseLeadershipAsync, _leaderCts is replaced with an uncancelled CTS, so LeaderToken appears
    // active while IsLeader is false. Consumers must always gate on IsLeader before trusting the token.
    public CancellationToken LeaderToken => _leaderCts?.Token ?? new CancellationToken(canceled: true);

    /// <inheritdoc />
    public event Action? OnStartedLeading;

    /// <inheritdoc />
    public event Action? OnStoppedLeading;

    public PostgresLeaderElectionService(string connectionString, IOptions<PostgresLeaderElectionOptions> options)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        _connectionString = connectionString;
        _options = options.Value;
    }

    /// <summary>
    /// Internal constructor for unit testing — accepts options directly.
    /// </summary>
    internal PostgresLeaderElectionService(string connectionString, PostgresLeaderElectionOptions options)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        _connectionString = connectionString;
        _options = options;
    }

    /// <summary>
    /// Test-only constructor that accepts a pre-built NpgsqlConnection for mocking.
    /// </summary>
    internal PostgresLeaderElectionService(
        string connectionString,
        PostgresLeaderElectionOptions options,
        NpgsqlConnection lockConnection)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        _connectionString = connectionString;
        _options = options;
        _lockConnection = lockConnection;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _serviceCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _leaderCts = new CancellationTokenSource();

        _electionTask = RunElectionLoopAsync(_serviceCts.Token);

        Log.Information("PostgresLeaderElectionService started. RenewalInterval={RenewalInterval}, AcquireRetryInterval={AcquireRetryInterval}",
            _options.RenewalInterval, _options.AcquireRetryInterval);

        return Task.CompletedTask;
    }

    // TODO: Race condition — StopAsync and RunElectionLoopAsync can both invoke SafeInvokeStoppedLeading()
    // concurrently if the loop's exception handler calls LoseLeadershipAsync at the same moment StopAsync
    // reads _isLeader == true. Non-idempotent subscribers would see a double-fire. Consider guarding with
    // an Interlocked.CompareExchange or a dedicated lock around the leadership-loss path.
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_serviceCts is null || _electionTask is null)
            return;

        Log.Information("PostgresLeaderElectionService stopping");

        await _serviceCts.CancelAsync();

        if (_isLeader)
        {
            _isLeader = false;
            await _leaderCts!.CancelAsync();
            SafeInvokeStoppedLeading();
        }
        else if (_leaderCts is not null && !_leaderCts.IsCancellationRequested)
        {
            // Ensure LeaderToken is cancelled after stop even if we never held leadership
            await _leaderCts.CancelAsync();
        }

        try
        {
            await _electionTask.WaitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            // Expected during shutdown
        }

        await ReleaseLockConnectionAsync();
    }

    private async Task RunElectionLoopAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await EnsureConnectionAsync(stoppingToken);

                var acquired = await TryAcquireLockAsync(stoppingToken);

                if (acquired)
                {
                    if (!_isLeader)
                    {
                        _isLeader = true;
                        Log.Information("PostgresLeaderElectionService: Leadership ACQUIRED");
                        SafeInvokeStartedLeading();
                    }

                    // TODO: pg_try_advisory_lock is re-entrant — calling it N times on the same session
                    // requires N pg_advisory_unlock calls. If EnsureConnectionAsync reuses an open connection
                    // and the loop re-acquires, the lock count increments but ReleaseLockConnectionAsync only
                    // unlocks once. Connection disposal releases all session locks, but explicit unlock on
                    // graceful shutdown would be incomplete. Consider tracking acquisition count or skipping
                    // re-acquire when _isLeader is already true.

                    // Hold leadership — periodically verify the connection is alive
                    await HoldLeadershipAsync(stoppingToken);
                }
                else
                {
                    // Another instance holds the lock. Wait and retry.
                    if (_isLeader)
                    {
                        await LoseLeadershipAsync();
                    }

                    await Task.Delay(_options.AcquireRetryInterval, stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "PostgresLeaderElectionService: Error in election loop. Will retry after {Delay}",
                    _options.AcquireRetryInterval);

                // Connection likely dead — lose leadership
                if (_isLeader)
                {
                    await LoseLeadershipAsync();
                }

                await DisposeConnectionAsync();

                try
                {
                    await Task.Delay(_options.AcquireRetryInterval, stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }

        Log.Information("PostgresLeaderElectionService: Election loop exiting");
    }

    /// <summary>
    /// While holding the lock, periodically verify the connection is still alive.
    /// If verification fails, leadership is lost.
    /// </summary>
    private async Task HoldLeadershipAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested && _isLeader)
        {
            try
            {
                await Task.Delay(_options.RenewalInterval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            try
            {
                // Verify connection is alive and we still hold the lock
                var stillHolding = await VerifyLockHeldAsync(stoppingToken);
                if (!stillHolding)
                {
                    Log.Warning("PostgresLeaderElectionService: Lock verification failed — lost leadership");
                    await LoseLeadershipAsync();
                    await DisposeConnectionAsync();
                    return;
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "PostgresLeaderElectionService: Connection error during lock verification — losing leadership");
                await LoseLeadershipAsync();
                await DisposeConnectionAsync();
                return;
            }
        }
    }

    private async Task<bool> TryAcquireLockAsync(CancellationToken ct)
    {
        await using var cmd = _lockConnection!.CreateCommand();
        cmd.CommandText = "SELECT pg_try_advisory_lock(@key)";
        cmd.Parameters.AddWithValue("key", LockKey);
        var result = await cmd.ExecuteScalarAsync(ct);
        return result is true;
    }

    /// <summary>
    /// Verifies that the advisory lock is still held on the current connection.
    /// Uses pg_locks system view to confirm the lock ownership.
    /// </summary>
    // TODO: Add AND objsubid = 1 to distinguish bigint advisory locks from two-int advisory locks.
    // TODO: Use (int)(LockKey & 0xFFFFFFFF) or split into classid/objid halves to avoid silent truncation if LockKey exceeds int.MaxValue.
    private async Task<bool> VerifyLockHeldAsync(CancellationToken ct)
    {
        await using var cmd = _lockConnection!.CreateCommand();
        cmd.CommandText = "SELECT 1"; // Connection liveness check
        await cmd.ExecuteScalarAsync(ct);

        // Advisory locks are session-scoped. If SELECT 1 succeeds, the connection
        // is alive and the lock is still held. But let's explicitly verify.
        await using var verifyCmd = _lockConnection.CreateCommand();
        verifyCmd.CommandText = """
            SELECT EXISTS(
                SELECT 1 FROM pg_locks
                WHERE locktype = 'advisory'
                  AND classid = 0
                  AND objid = @key
                  AND pid = pg_backend_pid()
                  AND granted = true
            )
            """;
        verifyCmd.Parameters.AddWithValue("key", (int)LockKey);
        var result = await verifyCmd.ExecuteScalarAsync(ct);
        return result is true;
    }

    private async Task EnsureConnectionAsync(CancellationToken ct)
    {
        if (_lockConnection is { State: System.Data.ConnectionState.Open })
            return;

        // Dispose any broken connection
        await DisposeConnectionAsync();

        _lockConnection = new NpgsqlConnection(_connectionString);
        await _lockConnection.OpenAsync(ct);
        Log.Debug("PostgresLeaderElectionService: Dedicated lock connection opened");
    }

    // TODO: Dispose the old _leaderCts before replacing it to avoid CancellationTokenSource leak on repeated leadership transitions.
    private async Task LoseLeadershipAsync()
    {
        _isLeader = false;
        if (_leaderCts is not null)
        {
            await _leaderCts.CancelAsync();
            SafeInvokeStoppedLeading();
            // Create fresh CTS for next leadership term
            _leaderCts = new CancellationTokenSource();
        }

        Log.Information("PostgresLeaderElectionService: Leadership LOST");
    }

    private async Task DisposeConnectionAsync()
    {
        if (_lockConnection is not null)
        {
            try
            {
                await _lockConnection.DisposeAsync();
            }
            catch (Exception ex)
            {
                Log.Debug(ex, "PostgresLeaderElectionService: Error disposing lock connection (expected if broken)");
            }

            _lockConnection = null;
        }
    }

    // TODO: Pass a timeout-based CancellationToken to ExecuteScalarAsync to prevent hanging during degraded shutdown.
    private async Task ReleaseLockConnectionAsync()
    {
        if (_lockConnection is null)
            return;

        try
        {
            if (_lockConnection.State == System.Data.ConnectionState.Open)
            {
                await using var cmd = _lockConnection.CreateCommand();
                cmd.CommandText = "SELECT pg_advisory_unlock(@key)";
                cmd.Parameters.AddWithValue("key", LockKey);
                await cmd.ExecuteScalarAsync();
                Log.Debug("PostgresLeaderElectionService: Advisory lock explicitly released");
            }
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "PostgresLeaderElectionService: Failed to explicitly release lock (connection close will release it)");
        }
        finally
        {
            await DisposeConnectionAsync();
        }
    }

    private void SafeInvokeStartedLeading()
    {
        try
        {
            OnStartedLeading?.Invoke();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error in OnStartedLeading handler");
        }
    }

    private void SafeInvokeStoppedLeading()
    {
        try
        {
            OnStoppedLeading?.Invoke();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error in OnStoppedLeading handler");
        }
    }

    /// <summary>
    /// Test-only: simulates leadership acquisition by setting state and firing events.
    /// Allows unit tests to verify event wiring and state transitions without a real DB.
    /// </summary>
    internal void SimulateAcquireForTesting()
    {
        _leaderCts ??= new CancellationTokenSource();
        _isLeader = true;
        SafeInvokeStartedLeading();
    }

    /// <summary>
    /// Test-only: simulates leadership loss by transitioning state and firing events.
    /// Allows unit tests to verify event wiring and state transitions without a real DB.
    /// </summary>
    internal async Task SimulateLoseForTestingAsync()
    {
        if (_isLeader)
        {
            await LoseLeadershipAsync();
        }
    }

    // TODO: If StopAsync is never called (e.g., service resolved and disposed directly in tests without
    // the host), _electionTask may still be running and could access _lockConnection after disposal.
    // Consider cancelling _serviceCts here as a safety net, or implementing IAsyncDisposable.
    public void Dispose()
    {
        _serviceCts?.Dispose();
        _leaderCts?.Dispose();
        _lockConnection?.Dispose();
    }
}
