using k8s;
using k8s.LeaderElection;
using k8s.LeaderElection.ResourceLock;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Serilog;

namespace CodingAgent.Pipeline.LeaderElection;

/// <summary>
/// Singleton IHostedService that performs K8s Lease-based leader election.
/// Each host that registers it gates its leader-only background services on it,
/// so only the leader replica runs them.
///
/// Exposes <see cref="IsLeader"/> and <see cref="LeaderToken"/>. The token is cancelled whenever
/// this instance is not the leader, so dependent services stop gracefully.
///
/// Intentionally keeps the namespace <c>CodingAgent.Pipeline.LeaderElection</c> even
/// though the class lives in <c>CodingAgent.Kubernetes</c>. This avoids touching
/// the ~50 files that import that namespace for types that remain in Pipeline.
/// </summary>
public sealed class LeaderElectionService : ILeaderElectionService, IHostedService, IDisposable
{
    private static readonly ILogger Log = Serilog.Log.ForContext<LeaderElectionService>();

    private readonly LeaderElectionOptions _options;
    private readonly Func<string, string, ILock>? _lockFactory;
    private readonly bool _isKubernetesEnvironment;
    private readonly Lock _termLock = new();

    private CancellationTokenSource? _leaderCts;
    private CancellationTokenSource? _serviceCts;
    private Task? _electionTask;

    private volatile bool _isLeader;

    /// <summary>
    /// True when this instance currently holds the leader lease.
    /// </summary>
    public bool IsLeader => _isLeader;

    /// <summary>
    /// Fires when leadership is acquired. Subscribers can start leader-only work.
    /// </summary>
    public event Action? OnStartedLeading;

    /// <summary>
    /// Fires when leadership is lost. Subscribers should stop leader-only work.
    /// </summary>
    public event Action? OnStoppedLeading;

    /// <summary>
    /// A CancellationToken that is cancelled whenever <see cref="IsLeader"/> is false:
    /// before the first acquisition, after leadership is lost and after the service stops.
    /// Each leadership term gets a new token.
    /// Dependent services should pass this token to their work loops.
    /// </summary>
    public CancellationToken LeaderToken => _leaderCts?.Token ?? new CancellationToken(canceled: true);

    public LeaderElectionService(IOptions<LeaderElectionOptions> options, IKubernetes? kubeClient = null)
        : this(options, CreateLeaseLockFactory(kubeClient, options.Value.LeaseName))
    {
    }

    /// <summary>
    /// Test seam: builds the service on a custom <see cref="ILock"/> instead of a Kubernetes
    /// <see cref="LeaseLock"/>. A null factory means "not running in Kubernetes".
    /// </summary>
    /// <param name="options">Leader election options.</param>
    /// <param name="lockFactory">Creates the lock from (namespace, identity).</param>
    internal LeaderElectionService(IOptions<LeaderElectionOptions> options, Func<string, string, ILock>? lockFactory)
    {
        _options = options.Value;
        _lockFactory = lockFactory;
        _isKubernetesEnvironment = lockFactory is not null;
    }

    private static Func<string, string, ILock>? CreateLeaseLockFactory(IKubernetes? kubeClient, string leaseName)
    {
        if (kubeClient is null)
            return null;
        return (ns, identity) => new LeaseLock(kubeClient, ns, leaseName, identity);
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (!_isKubernetesEnvironment)
        {
            if (_options.FailOnNonKubernetesEnvironment)
            {
                Log.Error("LeaderElectionService configured to fail outside K8s but no IKubernetes client available. " +
                          "Ensure the application is running in a Kubernetes cluster or set LeaderElection:FailOnNonKubernetesEnvironment to false");
                throw new InvalidOperationException(
                    "LeaderElectionService requires a Kubernetes environment but none was detected.");
            }

            Log.Warning("LeaderElectionService: Not running in Kubernetes environment. " +
                        "This instance will NOT become leader. Leader-dependent services will not run");
            _isLeader = false;
            return Task.CompletedTask;
        }

        _serviceCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        _electionTask = RunElectionLoopAsync(_serviceCts.Token);

        Log.Information("LeaderElectionService started. Lease={LeaseName}, Namespace={Namespace}, Identity={Identity}",
            _options.LeaseName, ResolveNamespace(), ResolveIdentity());

        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_serviceCts is null || _electionTask is null)
            return;

        Log.Information("LeaderElectionService stopping");

        // Signal the election loop to stop
        await _serviceCts.CancelAsync();

        // Cancel leader token so dependent services stop
        await EndLeadershipTermAsync();

        // Wait for election loop to complete (with timeout from host)
        try
        {
            await _electionTask.WaitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            // Expected during shutdown
        }
    }

    private async Task RunElectionLoopAsync(CancellationToken stoppingToken)
    {
        var identity = ResolveIdentity();
        var ns = ResolveNamespace();

        Log.Information("Leader election loop starting. Identity={Identity}, Namespace={Namespace}, Lease={LeaseName}",
            identity, ns, _options.LeaseName);

        var leaseLock = _lockFactory!(ns, identity);

        var config = new LeaderElectionConfig(leaseLock)
        {
            LeaseDuration = _options.LeaseDuration,
            RenewDeadline = _options.RenewDeadline,
            RetryPeriod = _options.RetryPeriod,
        };

        // Loop forever: acquire → hold → lose → re-acquire
        while (!stoppingToken.IsCancellationRequested)
        {
            using var elector = new LeaderElector(config);

            elector.OnStartedLeading += HandleStartedLeading;
            elector.OnStoppedLeading += HandleStoppedLeading;
            elector.OnNewLeader += leader =>
                Log.Information("Leader election: new leader observed: {Leader}", leader);
            elector.OnError += ex =>
                Log.Warning(ex, "Leader election error during acquire/renew");

            try
            {
                await elector.RunUntilLeadershipLostAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                // Service is shutting down
                break;
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Leader election unexpected error. Will retry after {RetryPeriod}", _options.RetryPeriod);
            }
            finally
            {
                // Leadership was lost, the attempt failed, or the service is stopping. End the term (no-op when not leading).
                await EndLeadershipTermAsync();
            }

            if (!stoppingToken.IsCancellationRequested)
            {
                Log.Information("Leader election: leadership lost. Re-attempting acquisition after {RetryPeriod}",
                    _options.RetryPeriod);
                try
                {
                    await Task.Delay(_options.RetryPeriod, stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }

        Log.Information("Leader election loop exiting");
    }

    /// <summary>
    /// Ends the current leadership term exactly once: clears <see cref="IsLeader"/>, cancels the term's
    /// token, then raises <see cref="OnStoppedLeading"/>. No-op when this instance is not the leader.
    /// The cancelled source stays in place, so <see cref="LeaderToken"/> stays cancelled until the next term.
    /// </summary>
    private async Task EndLeadershipTermAsync()
    {
        CancellationTokenSource? termCts;
        lock (_termLock)
        {
            if (!_isLeader)
                return;
            _isLeader = false;
            termCts = _leaderCts;
        }

        if (termCts is not null)
            await termCts.CancelAsync();
        SafeInvokeStoppedLeading();
    }

    private void HandleStartedLeading()
    {
        Log.Information("LeaderElectionService: This instance is now the LEADER");
        // TODO: [WARNING] The previous term's CancellationTokenSource is overwritten here without being disposed.
        // EndLeadershipTermAsync cancels it but never disposes it, and Dispose() only disposes the current CTS.
        // Each completed term beyond the first leaks one CancellationTokenSource. Fix: capture the old value before
        // overwriting and dispose it after releasing the lock (or dispose termCts at the end of EndLeadershipTermAsync
        // after it has been cancelled). Consumers may still hold the old token, so dispose only after the cancel.
        lock (_termLock)
        {
            // Each term gets a fresh token. The previous term's token stays cancelled for anyone still holding it.
            _leaderCts = new CancellationTokenSource();
            _isLeader = true;
        }
        SafeInvokeStartedLeading();
    }

    private static void HandleStoppedLeading()
    {
        // Handled in the loop's finally block via EndLeadershipTermAsync.
        // The LeaderElector fires this before returning, so we just log here.
        Log.Information("LeaderElectionService: Leadership LOST (elector callback)");
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

    private string ResolveIdentity()
    {
        if (!string.IsNullOrWhiteSpace(_options.Identity))
            return _options.Identity;

        // Try POD_NAME first (Downward API), then HOSTNAME
        var podName = Environment.GetEnvironmentVariable("POD_NAME");
        if (!string.IsNullOrWhiteSpace(podName))
            return podName;

        var hostname = Environment.GetEnvironmentVariable("HOSTNAME");
        if (!string.IsNullOrWhiteSpace(hostname))
            return hostname;

        return Environment.MachineName;
    }

    private string ResolveNamespace()
    {
        if (!string.IsNullOrWhiteSpace(_options.Namespace))
            return _options.Namespace;

        // Try POD_NAMESPACE env var first
        var ns = Environment.GetEnvironmentVariable("POD_NAMESPACE");
        if (!string.IsNullOrWhiteSpace(ns))
            return ns;

        // Try reading from mounted service account namespace file
        const string nsFile = "/var/run/secrets/kubernetes.io/serviceaccount/namespace";
        try
        {
            if (File.Exists(nsFile))
                return File.ReadAllText(nsFile).Trim();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to read namespace from {Path}", nsFile);
        }

        return "default";
    }

    public void Dispose()
    {
        _serviceCts?.Dispose();
        _leaderCts?.Dispose();
        GC.SuppressFinalize(this);
    }
}
