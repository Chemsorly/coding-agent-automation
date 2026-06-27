using k8s;
using k8s.LeaderElection;
using k8s.LeaderElection.ResourceLock;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Serilog;

namespace CodingAgentWebUI.Orchestration.LeaderElection;

/// <summary>
/// Singleton IHostedService that performs K8s Lease-based leader election.
/// Shared between PipelineLoopService, DispatchService, and ReconciliationService.
/// Only the leader replica runs these services.
/// 
/// Exposes <see cref="IsLeader"/> property and <see cref="LeaderToken"/> which is cancelled
/// when leadership is lost, allowing dependent services to stop gracefully.
/// </summary>
public sealed class LeaderElectionService : IHostedService, IDisposable
{
    private static readonly ILogger Log = Serilog.Log.ForContext<LeaderElectionService>();

    private readonly LeaderElectionOptions _options;
    private readonly IKubernetes? _kubeClient;
    private readonly bool _isKubernetesEnvironment;

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
    /// A CancellationToken that is valid while this instance is the leader.
    /// Cancelled when leadership is lost or the service is stopping.
    /// Dependent services should pass this token to their work loops.
    /// </summary>
    public CancellationToken LeaderToken => _leaderCts?.Token ?? new CancellationToken(canceled: true);

    public LeaderElectionService(IOptions<LeaderElectionOptions> options, IKubernetes? kubeClient = null)
    {
        _options = options.Value;
        _kubeClient = kubeClient;
        _isKubernetesEnvironment = kubeClient is not null;
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
        _leaderCts = new CancellationTokenSource();

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
        if (_isLeader)
        {
            _isLeader = false;
            await _leaderCts!.CancelAsync();
            SafeInvokeStoppedLeading();
        }

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

        var leaseLock = new LeaseLock(_kubeClient!, ns, _options.LeaseName, identity);

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

            // Leadership was lost (or error). Ensure we signal dependent services.
            if (_isLeader)
            {
                _isLeader = false;
                await _leaderCts!.CancelAsync();
                SafeInvokeStoppedLeading();
                // Create fresh CTS for next leadership term
                _leaderCts = new CancellationTokenSource();
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

    private void HandleStartedLeading()
    {
        Log.Information("LeaderElectionService: This instance is now the LEADER");
        _isLeader = true;
        SafeInvokeStartedLeading();
    }

    private void HandleStoppedLeading()
    {
        // Handled in the loop after RunUntilLeadershipLostAsync returns.
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
    }
}
