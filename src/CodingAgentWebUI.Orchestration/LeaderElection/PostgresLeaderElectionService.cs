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

    private volatile bool _isLeader;

    /// <inheritdoc />
    public bool IsLeader => _isLeader;

    /// <inheritdoc />
    public CancellationToken LeaderToken => _leaderCts?.Token ?? new CancellationToken(canceled: true);

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

    // TODO: StartAsync overwrites _serviceCts and _leaderCts without disposing previous instances.
    // If the service is started, stopped, and started again, the CancellationTokenSources from
    // the first lifecycle are leaked. Consider guarding against multiple starts or disposing old CTS.
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _serviceCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _leaderCts = new CancellationTokenSource();

        _electionTask = RunElectionLoopAsync(_serviceCts.Token);

        Log.Information(
            "PostgresLeaderElectionService started. LockKey={LockKey}, RenewalInterval={RenewalInterval}",
            _options.LockKey, _options.RenewalInterval);

        return Task.CompletedTask;
    }

    // TODO: Race condition between StopAsync and RunElectionLoopAsync: after _serviceCts.CancelAsync()
    // is called, the election loop may concurrently invoke HandleLeadershipLostAsync() (which sets
    // _isLeader = false and replaces _leaderCts). If this happens before StopAsync's if (_isLeader)
    // check, StopAsync skips cancelling the leader token. Consider synchronizing with a lock or
    // using Interlocked.Exchange for the _isLeader transition.
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
                // Ensure connection is open
                if (!await EnsureConnectionAsync(stoppingToken))
                {
                    await DelayWithCancellation(_options.RetryDelay, stoppingToken);
                    continue;
                }

                // Try to acquire the advisory lock
                var acquired = await _lockConnection!.TryAcquireLockAsync(_options.LockKey, stoppingToken);

                if (acquired && !_isLeader)
                {
                    // Transition to leader
                    _isLeader = true;
                    Log.Information("PostgresLeaderElectionService: Leadership ACQUIRED via advisory lock");
                    SafeInvokeStartedLeading();
                }
                else if (!acquired && _isLeader)
                {
                    // Lost leadership (shouldn't normally happen unless connection was recycled)
                    await HandleLeadershipLostAsync();
                }
                else if (!acquired)
                {
                    Log.Debug("PostgresLeaderElectionService: Lock not acquired, another replica is leader");
                }

                // If we're the leader, periodically verify the lock is still held
                if (_isLeader)
                {
                    await VerifyLockHeldLoopAsync(stoppingToken);
                    // If we exit the verify loop, we lost leadership
                    if (_isLeader)
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

                if (_isLeader)
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
        while (!stoppingToken.IsCancellationRequested && _isLeader)
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

    // TODO: CancellationTokenSource leak in HandleLeadershipLostAsync: the old _leaderCts is
    // cancelled but never disposed before being replaced with a new instance. Each leadership
    // loss leaks a CTS. Should dispose the old CTS before reassigning.
    private async Task HandleLeadershipLostAsync()
    {
        _isLeader = false;
        Log.Information("PostgresLeaderElectionService: Leadership LOST");
        await _leaderCts!.CancelAsync();
        SafeInvokeStoppedLeading();
        // Create fresh CTS for next leadership term
        _leaderCts = new CancellationTokenSource();
    }

    // TODO: ExecuteNonQueryAsync (ReleaseLockAsync) called without a CancellationToken.
    // If the connection is hung during graceful shutdown, this could block StopAsync indefinitely.
    // Consider passing the shutdown cancellation token through to ReleaseLockConnectionAsync.
    private async Task ReleaseLockConnectionAsync()
    {
        if (_lockConnection is null)
            return;

        try
        {
            if (_lockConnection.State == ConnectionState.Open)
            {
                await _lockConnection.ReleaseLockAsync(_options.LockKey);
                Log.Debug("PostgresLeaderElectionService: Advisory lock explicitly released");
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex,
                "PostgresLeaderElectionService: Failed to explicitly release advisory lock — connection close will release it");
        }
        finally
        {
            await CloseConnectionAsync();
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
        _leaderCts?.Dispose();
        (_lockConnection as IDisposable)?.Dispose();
    }
}
