using System.Data;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Serilog;

namespace CodingAgentWebUI.Orchestration.LeaderElection;

/// <summary>
/// Leader election backend using PostgreSQL advisory locks (<c>pg_try_advisory_lock</c>).
/// Designed for DB+SignalR multi-replica deployments without Kubernetes.
///
/// Advisory locks are session-scoped — if the connection drops, the lock is automatically released.
/// Uses a dedicated NpgsqlConnection (not from the EF Core pool) to hold the lock.
/// </summary>
public sealed class PostgresLeaderElectionService : ILeaderElectionService, IHostedService, IDisposable
{
    private static readonly ILogger Log = Serilog.Log.ForContext<PostgresLeaderElectionService>();

    private readonly IAdvisoryLockConnectionFactory _connectionFactory;
    private readonly PostgresLeaderElectionOptions _options;

    private IAdvisoryLockConnection? _lockConnection;
    private CancellationTokenSource? _leaderCts;
    private CancellationTokenSource? _serviceCts;
    private Task? _electionTask;

    private int _leaderState; // 0 = follower, 1 = leader

    /// <inheritdoc />
    public bool IsLeader => Volatile.Read(ref _leaderState) == 1;

    /// <inheritdoc />
    /// TODO: After StopAsync nulls _leaderCts via Interlocked.Exchange, this property returns
    /// a freshly-constructed canceled token (not the original CTS token). Callers who captured
    /// LeaderToken before stop will see cancellation (correct), but callers reading after stop
    /// get a structurally different token. This is acceptable but worth documenting for consumers.
    public CancellationToken LeaderToken =>
        Interlocked.CompareExchange(ref _leaderCts, null, null)?.Token
        ?? new CancellationToken(canceled: true);

    /// <inheritdoc />
    public event Action? OnStartedLeading;

    /// <inheritdoc />
    public event Action? OnStoppedLeading;

    public PostgresLeaderElectionService(string connectionString, IOptions<PostgresLeaderElectionOptions> options)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        _connectionFactory = new NpgsqlAdvisoryLockConnectionFactory(connectionString);
        _options = options.Value;
    }

    /// <summary>
    /// Internal constructor for unit testing — accepts options and a connection factory directly.
    /// </summary>
    internal PostgresLeaderElectionService(
        PostgresLeaderElectionOptions options,
        IAdvisoryLockConnectionFactory connectionFactory)
    {
        _options = options;
        _connectionFactory = connectionFactory;
    }

    /// <summary>
    /// Internal constructor for unit testing — accepts a pre-built connection and options directly.
    /// The connection string is only used for validation; the <paramref name="testConnection"/> is used directly.
    /// </summary>
    internal PostgresLeaderElectionService(
        string connectionString,
        PostgresLeaderElectionOptions options,
        IAdvisoryLockConnection? testConnection = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        _connectionFactory = new NpgsqlAdvisoryLockConnectionFactory(connectionString);
        _options = options;
        _lockConnection = testConnection;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        // TODO: _serviceCts?.Dispose() is not thread-safe — if StopAsync is concurrently awaiting
        // _serviceCts.CancelAsync(), this could cause ObjectDisposedException. The IHostedService
        // contract prevents concurrent Start/Stop, but consider using Interlocked.Exchange for
        // defensive safety consistent with _leaderCts handling.
        _serviceCts?.Dispose();
        Interlocked.Exchange(ref _leaderCts, null)?.Dispose();

        // Defensive reset: if StartAsync is called without a preceding StopAsync (e.g., crash
        // recovery or host restart), stale _leaderState=1 would skip leadership acquisition.
        Volatile.Write(ref _leaderState, 0);

        _serviceCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _leaderCts = new CancellationTokenSource();

        _electionTask = RunElectionLoopAsync(_serviceCts.Token);

        Log.Information(
            "PostgresLeaderElectionService started. LockKey={LockKey}, RenewalInterval={RenewalInterval}",
            _options.LockKey, _options.RenewalInterval);

        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_serviceCts is null || _electionTask is null)
            return;

        Log.Information("PostgresLeaderElectionService stopping");

        await _serviceCts.CancelAsync();

        if (Interlocked.CompareExchange(ref _leaderState, 0, 1) == 1)
        {
            // We won the race — we are responsible for cleanup
            var cts = Interlocked.Exchange(ref _leaderCts, null);
            if (cts is not null)
            {
                await cts.CancelAsync();
                cts.Dispose();
            }
            SafeInvokeStoppedLeading();
        }

        try
        {
            await _electionTask.WaitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            // Expected during shutdown
        }

        await ReleaseLockConnectionAsync(cancellationToken);
    }

    private async Task RunElectionLoopAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // Ensure connection is open
                if (!await EnsureConnectionAsync(stoppingToken))
                {
                    await DelayWithCancellation(_options.RetryDelay, stoppingToken);
                    continue;
                }

                // Try to acquire the advisory lock
                var acquired = await _lockConnection!.TryAcquireLockAsync(_options.LockKey, stoppingToken);

                if (acquired && !IsLeader)
                {
                    // Transition to leader — CAS ensures we don't overwrite if StopAsync raced ahead
                    if (Interlocked.CompareExchange(ref _leaderState, 1, 0) == 0)
                    {
                        Log.Information("PostgresLeaderElectionService: Leadership ACQUIRED via advisory lock");
                        SafeInvokeStartedLeading();
                    }
                    else
                    {
                        Log.Debug("PostgresLeaderElectionService: Lock acquired but state transition blocked (concurrent stop)");
                    }
                }
                else if (!acquired && IsLeader)
                {
                    // Lost leadership (shouldn't normally happen unless connection was recycled)
                    await HandleLeadershipLostAsync();
                }
                else if (!acquired)
                {
                    Log.Debug("PostgresLeaderElectionService: Lock not acquired, another replica is leader");
                }

                // If we're the leader, periodically verify the lock is still held
                if (IsLeader)
                {
                    await VerifyLockHeldLoopAsync(stoppingToken);
                    // If we exit the verify loop, we lost leadership
                    if (IsLeader)
                    {
                        await HandleLeadershipLostAsync();
                    }
                }
                else
                {
                    // Not leader — wait before retrying
                    await DelayWithCancellation(_options.RetryDelay, stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "PostgresLeaderElectionService: Error in election loop. Retrying after {RetryDelay}",
                    _options.RetryDelay);

                if (IsLeader)
                {
                    await HandleLeadershipLostAsync();
                }

                await CloseConnectionAsync();
                await DelayWithCancellation(_options.RetryDelay, stoppingToken);
            }
        }

        Log.Information("PostgresLeaderElectionService: Election loop exiting");
    }

    /// <summary>
    /// Continuously verifies the advisory lock is held by checking connection state
    /// and re-checking the lock periodically.
    /// Exits when the lock is lost or the service is stopping.
    /// </summary>
    private async Task VerifyLockHeldLoopAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested && IsLeader)
        {
            await DelayWithCancellation(_options.RenewalInterval, stoppingToken);

            if (stoppingToken.IsCancellationRequested)
                break;

            // Check if connection is still alive
            if (_lockConnection is null || _lockConnection.State != ConnectionState.Open)
            {
                Log.Warning("PostgresLeaderElectionService: Lock connection lost — leadership lost");
                break;
            }

            // Verify lock is still held by querying pg_locks
            try
            {
                var stillHeld = await _lockConnection.VerifyLockIsHeldAsync(_options.LockKey, stoppingToken);
                if (!stillHeld)
                {
                    Log.Warning("PostgresLeaderElectionService: Advisory lock no longer held — leadership lost");
                    break;
                }
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                Log.Warning(ex, "PostgresLeaderElectionService: Error verifying lock — assuming lost");
                break;
            }
        }
    }

    private async Task<bool> EnsureConnectionAsync(CancellationToken ct)
    {
        if (_lockConnection is not null && _lockConnection.State == ConnectionState.Open)
            return true;

        // Close existing broken connection
        await CloseConnectionAsync();

        try
        {
            _lockConnection = _connectionFactory.Create();
            await _lockConnection.OpenAsync(ct);
            Log.Debug("PostgresLeaderElectionService: Dedicated lock connection opened");
            return true;
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            Log.Warning(ex, "PostgresLeaderElectionService: Failed to open lock connection");
            if (_lockConnection is not null)
            {
                await _lockConnection.DisposeAsync();
            }
            _lockConnection = null;
            return false;
        }
    }

    private async Task HandleLeadershipLostAsync()
    {
        if (Interlocked.CompareExchange(ref _leaderState, 0, 1) != 1)
            return; // Another path already transitioned — nothing to do

        Log.Information("PostgresLeaderElectionService: Leadership LOST");

        var old = Interlocked.Exchange(ref _leaderCts, new CancellationTokenSource());
        if (old is not null)
        {
            await old.CancelAsync();
            old.Dispose();
        }
        SafeInvokeStoppedLeading();
    }

    private async Task ReleaseLockConnectionAsync(CancellationToken shutdownToken = default)
    {
        if (_lockConnection is null)
            return;

        // Bound the entire release+close operation to 5 seconds.
        // If Postgres is unreachable, we don't want to block host shutdown.
        // Advisory locks are session-scoped — if we can't release explicitly,
        // closing the connection (or the connection dropping) releases them automatically.
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(shutdownToken);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(5));

        try
        {
            if (_lockConnection.State == ConnectionState.Open)
            {
                await _lockConnection.ReleaseLockAsync(_options.LockKey, timeoutCts.Token);
                Log.Debug("PostgresLeaderElectionService: Advisory lock explicitly released");
            }
        }
        catch (OperationCanceledException)
        {
            Log.Warning(
                "PostgresLeaderElectionService: Timed out releasing advisory lock — connection close will release it");
        }
        catch (Exception ex)
        {
            Log.Warning(ex,
                "PostgresLeaderElectionService: Failed to explicitly release advisory lock — connection close will release it");
        }
        finally
        {
            await CloseConnectionBoundedAsync();
        }
    }

    /// <summary>
    /// Bounded connection close for the shutdown path. If CloseAsync hangs (e.g., Postgres unreachable),
    /// we abandon the close after 2 seconds. The underlying CloseAsync task may continue running in the
    /// background — this is acceptable since the process is shutting down and the connection will be
    /// cleaned up by the OS / GC. Advisory locks are session-scoped and released server-side when the
    /// TCP connection drops.
    /// </summary>
    private async Task CloseConnectionBoundedAsync()
    {
        if (_lockConnection is null)
            return;

        try
        {
            // Fresh timeout (not linked to shutdown token) — the 5s release timeout may have
            // already fired, so we use an independent 2s budget for the close operation.
            // TODO: If CloseAsync() hangs and is abandoned via WaitAsync, the task's exception
            // (if any) goes unobserved and may surface as UnobservedTaskException in applications
            // that subscribe to that event. Consider attaching a continuation to suppress:
            // closeTask.ContinueWith(_ => { }, TaskContinuationOptions.OnlyOnFaulted)
            using var closeCts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await _lockConnection.CloseAsync().WaitAsync(closeCts.Token);
            await _lockConnection.DisposeAsync();
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "PostgresLeaderElectionService: Error closing lock connection during shutdown");
            // Best-effort dispose even if close failed/timed out
            try { _lockConnection.Dispose(); } catch { /* swallow */ }
        }
        finally
        {
            _lockConnection = null;
        }
    }

    private async Task CloseConnectionAsync()
    {
        if (_lockConnection is null)
            return;

        try
        {
            await _lockConnection.CloseAsync();
            await _lockConnection.DisposeAsync();
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "PostgresLeaderElectionService: Error closing lock connection");
        }
        finally
        {
            _lockConnection = null;
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

    private static async Task DelayWithCancellation(TimeSpan delay, CancellationToken ct)
    {
        try
        {
            await Task.Delay(delay, ct);
        }
        catch (OperationCanceledException)
        {
            // Expected — service is stopping or test is completing
        }
    }

    public void Dispose()
    {
        _serviceCts?.Dispose();
        Interlocked.Exchange(ref _leaderCts, null)?.Dispose();
        (_lockConnection as IDisposable)?.Dispose();
    }
}
