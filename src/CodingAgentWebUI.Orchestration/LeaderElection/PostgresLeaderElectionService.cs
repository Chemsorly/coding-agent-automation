using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Npgsql;
using Serilog;

namespace CodingAgentWebUI.Orchestration.LeaderElection;

/// <summary>
/// Leader election backend using PostgreSQL session-scoped advisory locks.
/// Used in DB+SignalR mode when Kubernetes is not available.
///
/// Key properties:
/// - Uses a dedicated <see cref="NpgsqlConnection"/> (NOT from the EF Core pool)
/// - Leadership is automatically lost when the connection drops (session-scoped lock)
/// - Periodically verifies the lock is still held and connection is alive
/// - Re-acquires leadership after reconnection
/// </summary>
public sealed class PostgresLeaderElectionService : ILeaderElectionService, IHostedService, IDisposable
{
    private static readonly ILogger Log = Serilog.Log.ForContext<PostgresLeaderElectionService>();

    /// <summary>
    /// Well-known advisory lock key. Chosen to avoid collision with user advisory locks.
    /// 0x_0CAA_1EAD = "CAA LEAD(er)" — a memorable, unique constant.
    /// </summary>
    internal const long LockKey = 0x0CAA_1EAD;

    private readonly string _connectionString;
    private readonly PostgresLeaderElectionOptions _options;
    private readonly object _leadershipLock = new();

    private NpgsqlConnection? _lockConnection;
    private CancellationTokenSource? _leaderCts;
    private CancellationTokenSource? _serviceCts;
    private Task? _electionTask;

    private volatile bool _isLeader;

    /// <inheritdoc/>
    public bool IsLeader => _isLeader;

    // TODO: LeaderToken reads _leaderCts?.Token without synchronization. A consumer could access
    // a disposed-but-not-yet-null CTS between dispose and null assignment in the election loop.
    // The existing K8s implementation has the same pattern. Consider capturing the token in a
    // volatile field on leadership transitions to avoid this window.
    /// <inheritdoc/>
    public CancellationToken LeaderToken => _leaderCts?.Token ?? new CancellationToken(canceled: true);

    /// <inheritdoc/>
    public event Action? OnStartedLeading;

    /// <inheritdoc/>
    public event Action? OnStoppedLeading;

    /// <summary>
    /// Internal test hooks — when set, these bypass real DB operations.
    /// Allows testing the full state machine without requiring a real Postgres connection.
    /// </summary>
    internal Func<CancellationToken, Task<bool>>? TestEnsureConnectionHook;
    internal Func<CancellationToken, Task<bool>>? TestTryAcquireLockHook;
    internal Func<CancellationToken, Task<bool>>? TestVerifyLockHeldHook;

    // TODO: Add ArgumentNullException.ThrowIfNull(options) to throw a proper ArgumentNullException
    // with the parameter name rather than a NullReferenceException on options.Value access.
    public PostgresLeaderElectionService(string connectionString, IOptions<PostgresLeaderElectionOptions> options)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        _connectionString = connectionString;
        _options = options.Value;
    }

    /// <summary>
    /// Internal constructor for testing — accepts options directly.
    /// </summary>
    // TODO: [REVIEW] Add null guard: ArgumentNullException.ThrowIfNull(options) to throw a properly
    // named ArgumentNullException instead of NullReferenceException on options member access.
    internal PostgresLeaderElectionService(string connectionString, PostgresLeaderElectionOptions options)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        _connectionString = connectionString;
        _options = options;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _serviceCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        // Note: _leaderCts is NOT created here — it's only created when leadership is acquired.
        // This ensures LeaderToken returns a cancelled token until this instance is actually leader.

        _electionTask = RunElectionLoopAsync(_serviceCts.Token);

        Log.Information("PostgresLeaderElectionService started. LockKey={LockKey}, RenewalInterval={Interval}",
            LockKey, _options.RenewalInterval);

        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_serviceCts is null || _electionTask is null)
            return;

        Log.Information("PostgresLeaderElectionService stopping");

        // Signal the election loop to stop — the loop handles its own leadership transition
        await _serviceCts.CancelAsync();

        // Wait for election loop to complete. The loop owns the leadership transition
        // (setting _isLeader=false, cancelling _leaderCts, firing OnStoppedLeading).
        // This ordering eliminates the race condition between StopAsync and the loop.
        try
        {
            await _electionTask.WaitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            // Expected during shutdown
        }

        // After the loop has exited, release the lock and close the connection.
        // No race: the loop is done, we have exclusive access to the connection.
        await ReleaseLockAsync();
        await CloseConnectionAsync();
    }

    private async Task RunElectionLoopAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // Ensure we have a live connection
                if (!await EnsureConnectionAsync(stoppingToken))
                {
                    // Connection failed — wait and retry
                    await DelayOrStop(_options.RetryInterval, stoppingToken);
                    continue;
                }

                // Try to acquire the advisory lock
                var acquired = await TryAcquireLockAsync(stoppingToken);

                if (acquired)
                {
                    // We got leadership
                    if (!_isLeader)
                    {
                        TransitionToLeader();
                    }

                    // Hold leadership: periodically verify lock is still held
                    await HoldLeadershipAsync(stoppingToken);

                    // TODO: [REVIEW] Advisory lock re-entrancy concern. If VerifyLockHeldAsync throws a transient
                    // exception (e.g., brief query timeout) but the connection is still alive, HoldLeadershipAsync
                    // exits and we call TransitionFromLeader + fire OnStoppedLeading. On the next iteration,
                    // TryAcquireLockAsync re-acquires re-entrantly (lock was never released — session-scoped),
                    // incrementing Postgres's lock reference count, and fires OnStartedLeading again. This produces
                    // spurious leadership flaps disrupting dependent services. Consider adding a short retry in
                    // HoldLeadershipAsync before declaring leadership lost, or checking connection liveness before
                    // transitioning away from leader.
                    // Leadership was lost (connection dropped or lock released or stopping)
                    TransitionFromLeader();
                }
                else
                {
                    // Another replica holds the lock — wait and retry
                    Log.Debug("PostgresLeaderElectionService: Lock not acquired (another leader holds it). " +
                              "Retrying in {Interval}", _options.RetryInterval);
                }

                if (!stoppingToken.IsCancellationRequested)
                {
                    await DelayOrStop(_options.RetryInterval, stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                // Service is stopping — exit the loop.
                // If we were leader, HoldLeadershipAsync already returned and TransitionFromLeader ran above.
                break;
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "PostgresLeaderElectionService: Error in election loop. Retrying in {Interval}",
                    _options.RetryInterval);

                // If we thought we were leader, we're not anymore
                TransitionFromLeader();

                // Close the potentially broken connection
                await CloseConnectionAsync();

                if (!stoppingToken.IsCancellationRequested)
                {
                    await DelayOrStop(_options.RetryInterval, stoppingToken);
                }
            }
        }

        Log.Information("PostgresLeaderElectionService: Election loop exiting");
    }

    /// <summary>
    /// Transitions to leader state. Thread-safe: uses lock to prevent double-transition.
    /// </summary>
    private void TransitionToLeader()
    {
        lock (_leadershipLock)
        {
            if (_isLeader)
                return;

            _leaderCts = new CancellationTokenSource();
            _isLeader = true;
        }

        Log.Information("PostgresLeaderElectionService: Leadership ACQUIRED");
        SafeInvokeStartedLeading();
    }

    /// <summary>
    /// Transitions from leader state. Thread-safe: uses lock to prevent double-transition.
    /// Synchronously cancels the leader CTS and fires the stopped-leading event.
    /// </summary>
    private void TransitionFromLeader()
    {
        CancellationTokenSource? leaderCts;

        lock (_leadershipLock)
        {
            if (!_isLeader)
                return;

            _isLeader = false;
            leaderCts = _leaderCts;
            _leaderCts = null;
        }

        // Cancel and dispose outside the lock to avoid holding it during event handlers
        // TODO: [REVIEW] leaderCts.Cancel() is synchronous and invokes all registered callbacks on
        // LeaderToken on the election loop thread. If a consumer registers a continuation that does
        // substantial work or awaits, it blocks the election loop. Consider using CancelAsync() (.NET 8+)
        // or documenting that OnStoppedLeading/LeaderToken callbacks must be non-blocking.
        if (leaderCts is not null)
        {
            leaderCts.Cancel();
            leaderCts.Dispose();
        }

        Log.Information("PostgresLeaderElectionService: Leadership LOST");
        SafeInvokeStoppedLeading();
    }

    /// <summary>
    /// Periodically verifies the lock is still held by checking connection health.
    /// Returns when leadership is lost or service is stopping.
    /// </summary>
    private async Task HoldLeadershipAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            await DelayOrStop(_options.RenewalInterval, stoppingToken);

            if (stoppingToken.IsCancellationRequested)
                break;

            try
            {
                // Verify connection is still alive and lock is still held
                if (!await VerifyLockHeldAsync(stoppingToken))
                {
                    Log.Warning("PostgresLeaderElectionService: Lock verification failed — leadership lost");
                    break;
                }
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "PostgresLeaderElectionService: Error during lock verification — assuming leadership lost");
                break;
            }
        }
    }

    /// <summary>
    /// Ensures the dedicated connection is open. Returns false if connection cannot be established.
    /// </summary>
    internal async Task<bool> EnsureConnectionAsync(CancellationToken ct)
    {
        if (TestEnsureConnectionHook is not null)
            return await TestEnsureConnectionHook(ct);

        if (_lockConnection is { State: System.Data.ConnectionState.Open })
            return true;

        // Close any stale connection
        await CloseConnectionAsync();

        try
        {
            _lockConnection = new NpgsqlConnection(_connectionString);
            await _lockConnection.OpenAsync(ct);
            Log.Debug("PostgresLeaderElectionService: Dedicated connection opened");
            return true;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "PostgresLeaderElectionService: Failed to open dedicated connection");
            _lockConnection?.Dispose();
            _lockConnection = null;
            return false;
        }
    }

    /// <summary>
    /// Attempts to acquire the session-scoped advisory lock. Non-blocking.
    /// </summary>
    internal async Task<bool> TryAcquireLockAsync(CancellationToken ct)
    {
        if (TestTryAcquireLockHook is not null)
            return await TestTryAcquireLockHook(ct);

        if (_lockConnection is null || _lockConnection.State != System.Data.ConnectionState.Open)
            return false;

        await using var cmd = _lockConnection.CreateCommand();
        cmd.CommandText = "SELECT pg_try_advisory_lock(@key)";
        cmd.Parameters.AddWithValue("key", LockKey);

        var result = await cmd.ExecuteScalarAsync(ct);
        return result is true;
    }

    // TODO: VerifyLockHeldAsync splits the lock key into classid/objid as (int)(LockKey >> 32) and
    // (int)(LockKey & 0xFFFFFFFF). For current key value (0x0CAA_1EAD) this is safe, but if LockKey
    // is ever changed to a value where either half has bit 31 set, the signed int cast would produce
    // a negative number that won't match the unsigned oid column in pg_locks. Consider using
    // unchecked casts or documenting the constraint.
    /// <summary>
    /// Verifies the advisory lock is still held on the current connection.
    /// Uses pg_locks to check — also serves as a connection health check.
    /// </summary>
    internal async Task<bool> VerifyLockHeldAsync(CancellationToken ct)
    {
        if (TestVerifyLockHeldHook is not null)
            return await TestVerifyLockHeldHook(ct);

        if (_lockConnection is null || _lockConnection.State != System.Data.ConnectionState.Open)
            return false;

        await using var cmd = _lockConnection.CreateCommand();
        cmd.CommandText = """
            SELECT EXISTS(
                SELECT 1 FROM pg_locks
                WHERE locktype = 'advisory'
                  AND classid = @classid
                  AND objid = @objid
                  AND pid = pg_backend_pid()
                  AND granted = true
            )
            """;
        // Advisory lock key is stored as (classid, objid) where classid = high 32 bits, objid = low 32 bits
        // TODO: [REVIEW] Add 'AND objsubid = 1' to distinguish single-key (bigint) advisory locks from
        // two-key (int,int) advisory locks. Without this filter, a concurrent pg_try_advisory_lock(0, 213905069)
        // (two-key form with matching classid/objid values) would falsely satisfy this verification query.
        cmd.Parameters.AddWithValue("classid", (int)(LockKey >> 32));
        cmd.Parameters.AddWithValue("objid", (int)(LockKey & 0xFFFFFFFF));

        var result = await cmd.ExecuteScalarAsync(ct);
        return result is true;
    }

    // TODO: ReleaseLockAsync is called after the election loop exits but may race with connection
    // state if the connection was already closed by the loop's error handler. Current exception
    // handling makes this non-fatal, but consider checking connection state more defensively.
    /// <summary>
    /// Explicitly releases the advisory lock (best-effort).
    /// </summary>
    private async Task ReleaseLockAsync()
    {
        if (_lockConnection is null || _lockConnection.State != System.Data.ConnectionState.Open)
            return;

        try
        {
            await using var cmd = _lockConnection.CreateCommand();
            cmd.CommandText = "SELECT pg_advisory_unlock(@key)";
            cmd.Parameters.AddWithValue("key", LockKey);
            // TODO: [REVIEW] Use ExecuteScalarAsync() instead of ExecuteNonQueryAsync() — this is a
            // SELECT query returning a boolean. ExecuteNonQueryAsync still executes the statement and
            // releases the lock server-side, but returns -1 (meaningless for SELECT via Npgsql).
            // ExecuteScalarAsync would allow logging whether the lock was actually held.
            await cmd.ExecuteNonQueryAsync();
            Log.Debug("PostgresLeaderElectionService: Advisory lock released");
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "PostgresLeaderElectionService: Failed to explicitly release advisory lock — " +
                            "connection close will release it automatically");
        }
    }

    private async Task CloseConnectionAsync()
    {
        if (_lockConnection is null)
            return;

        try
        {
            if (_lockConnection.State != System.Data.ConnectionState.Closed)
                await _lockConnection.CloseAsync();
            await _lockConnection.DisposeAsync();
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "PostgresLeaderElectionService: Error closing connection (expected if already broken)");
            try { _lockConnection.Dispose(); } catch { /* best effort */ }
        }
        finally
        {
            _lockConnection = null;
        }
    }

    private static async Task DelayOrStop(TimeSpan delay, CancellationToken ct)
    {
        try
        {
            await Task.Delay(delay, ct);
        }
        catch (OperationCanceledException)
        {
            // Expected when stopping
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

    public void Dispose()
    {
        _serviceCts?.Dispose();
        _leaderCts?.Dispose();
        try
        {
            _lockConnection?.Dispose();
        }
        catch (InvalidOperationException)
        {
            // NpgsqlConnection throws if disposed while in Connecting state — safe to ignore
        }
    }
}
