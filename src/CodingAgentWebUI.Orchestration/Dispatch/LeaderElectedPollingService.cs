using System.Threading.RateLimiting;
using CodingAgentWebUI.Orchestration.LeaderElection;
using Microsoft.Extensions.Hosting;
using Serilog;

namespace CodingAgentWebUI.Orchestration.Dispatch;

/// <summary>
/// Abstract base class for background services that run under leader election.
/// Seals <see cref="BackgroundService.ExecuteAsync"/> and provides the shared
/// leader-wait / linked-CTS / poll-loop pattern. Subclasses override
/// <see cref="OnPollCycleAsync"/> for simple poll loops, or
/// <see cref="RunLeadershipTermAsync"/> for full control during the leadership term
/// (e.g., concurrent Watch + Poll loops in <see cref="ReconciliationService"/>).
///
/// Rate-limiting: subclasses that need per-cycle rate limiting override
/// <see cref="RateLimitPerSecond"/> (returning > 0) to get a shared
/// <see cref="TokenBucketRateLimiter"/> created and disposed by this base class.
/// </summary>
public abstract class LeaderElectedPollingService : BackgroundService
{
    private static readonly ILogger Log = Serilog.Log.ForContext<LeaderElectedPollingService>();

    private TokenBucketRateLimiter? _rateLimiter;

    /// <summary>
    /// The leader election service used to determine if this instance holds the leader lease.
    /// </summary>
    protected ILeaderElectionService LeaderElection { get; }

    /// <summary>
    /// Display name used in log messages. Each subclass provides its own name.
    /// </summary>
    protected abstract string ServiceName { get; }

    /// <summary>
    /// Interval in seconds between poll cycles. Each subclass provides its own value
    /// from its specific options class (e.g., DispatchServiceOptions or ReconciliationServiceOptions).
    /// </summary>
    protected abstract int PollIntervalSeconds { get; }

    /// <summary>
    /// Maximum operations per second for the shared rate limiter.
    /// Override and return a value &gt; 0 to enable rate limiting.
    /// Defaults to 0 (no rate limiter created).
    /// </summary>
    protected virtual int RateLimitPerSecond => 0;

    /// <summary>
    /// Shared <see cref="TokenBucketRateLimiter"/> instance created from <see cref="RateLimitPerSecond"/>.
    /// Returns null when <see cref="RateLimitPerSecond"/> is 0.
    /// Created lazily on first access; disposed by <see cref="Dispose"/>.
    /// Note: this lazy-init is not thread-safe; subclasses with concurrent loops should be aware.
    /// </summary>
    protected TokenBucketRateLimiter? RateLimiter
    {
        get
        {
            if (_rateLimiter is null && RateLimitPerSecond > 0)
                _rateLimiter = RateLimiterFactory.CreateTokenBucket(RateLimitPerSecond);
            return _rateLimiter;
        }
    }

    protected LeaderElectedPollingService(ILeaderElectionService leaderElection)
    {
        LeaderElection = leaderElection;
    }

    /// <summary>
    /// Sealed implementation of the leader-election poll-loop pattern.
    /// Waits for leadership, creates a linked CancellationToken (host stop OR leadership loss),
    /// delegates to <see cref="RunLeadershipTermAsync"/>, and re-enters the wait loop on leadership loss.
    /// </summary>
    protected sealed override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        Log.Information("{ServiceName} started — waiting for leader election", ServiceName);

        while (!stoppingToken.IsCancellationRequested)
        {
            // Wait for leadership (2s poll)
            while (!stoppingToken.IsCancellationRequested && !LeaderElection.IsLeader)
            {
                await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
            }

            if (stoppingToken.IsCancellationRequested) break;

            // Create linked token: cancels on EITHER host stop OR leadership loss
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(
                stoppingToken, LeaderElection.LeaderToken);
            var ct = linked.Token;

            try
            {
                await RunLeadershipTermAsync(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested && !stoppingToken.IsCancellationRequested)
            {
                // Leadership lost — fall through to re-enter wait loop.
                // Note: if both tokens cancel simultaneously the filter evaluates to false and the
                // OCE propagates; BackgroundService handles it cleanly as a normal service stop.
            }

            if (!stoppingToken.IsCancellationRequested)
            {
                Log.Information("{ServiceName}: leadership lost, re-entering wait loop", ServiceName);
            }
        }

        Log.Information("{ServiceName}: exiting (stopping)", ServiceName);
    }

    /// <summary>
    /// Called when leadership is acquired. The default implementation runs a simple poll loop
    /// calling <see cref="OnPollCycleAsync"/> with <see cref="PollIntervalSeconds"/> delay.
    /// Override for services that need full control during the leadership term
    /// (e.g., concurrent Watch + Poll loops).
    /// </summary>
    /// <param name="ct">Cancellation token that fires on leadership loss or host stop.</param>
    protected virtual async Task RunLeadershipTermAsync(CancellationToken ct)
    {
        Log.Information("{ServiceName}: leader acquired, entering poll loop", ServiceName);

        while (!ct.IsCancellationRequested)
        {
            try
            {
                await OnPollCycleAsync(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                Log.Error(ex, "{ServiceName}: unhandled error in poll cycle", ServiceName);
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(PollIntervalSeconds), ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    /// <summary>
    /// The work to perform each poll cycle. Called repeatedly by the default
    /// <see cref="RunLeadershipTermAsync"/> implementation.
    /// </summary>
    protected abstract Task OnPollCycleAsync(CancellationToken ct);

    /// <inheritdoc/>
    public override void Dispose()
    {
        _rateLimiter?.Dispose();
        base.Dispose();
    }
}
