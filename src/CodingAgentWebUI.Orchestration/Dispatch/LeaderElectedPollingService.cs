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
/// Optionally manages a <see cref="TokenBucketRateLimiter"/> for subclasses that need
/// rate limiting. Call <see cref="InitializeRateLimiter"/> from the subclass constructor
/// to opt in; the base class disposes it automatically.
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
    /// The rate limiter created by <see cref="InitializeRateLimiter"/>, or <c>null</c> if
    /// the subclass did not opt in to rate limiting.
    /// </summary>
    protected TokenBucketRateLimiter? RateLimiter => _rateLimiter;

    /// <summary>
    /// Display name used in log messages. Each subclass provides its own name.
    /// </summary>
    protected abstract string ServiceName { get; }

    /// <summary>
    /// Interval in seconds between poll cycles. Each subclass provides its own value
    /// from its specific options class (e.g., DispatchServiceOptions or ReconciliationServiceOptions).
    /// </summary>
    protected abstract int PollIntervalSeconds { get; }

    protected LeaderElectedPollingService(ILeaderElectionService leaderElection)
    {
        LeaderElection = leaderElection;
    }

    /// <summary>
    /// Creates and stores a <see cref="TokenBucketRateLimiter"/> for use during poll cycles.
    /// Call this once from the subclass constructor. The base class disposes the limiter
    /// automatically in <see cref="Dispose"/>.
    /// </summary>
    /// <param name="rateLimitPerSecond">Maximum tokens (job creations) per second.</param>
    protected void InitializeRateLimiter(int rateLimitPerSecond)
    {
        _rateLimiter = RateLimiterFactory.CreateTokenBucket(rateLimitPerSecond);
    }

    /// <inheritdoc/>
    public override void Dispose()
    {
        _rateLimiter?.Dispose();
        base.Dispose();
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
                // TODO: This Task.Delay is not wrapped in a try/catch. When the host stops while waiting
                // for leadership (before this instance ever acquires it), stoppingToken fires and the
                // OperationCanceledException propagates out of ExecuteAsync, bypassing the
                // Log.Information("{ServiceName}: exiting (stopping)") line below. BackgroundService
                // swallows the exception so there is no crash, but the graceful-shutdown log is silently
                // skipped. Consider wrapping in try/catch(OperationCanceledException) and breaking here.
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
                // Leadership lost — fall through to re-enter wait loop
                // TODO: This catch filter does not match when both ct and stoppingToken are cancelled
                // simultaneously (e.g., host shuts down at the exact moment leadership is lost). In that
                // case the OCE escapes ExecuteAsync uncaught. With the current default RunLeadershipTermAsync
                // this is unreachable (the inner loop swallows OCE and returns normally), but a future
                // subclass that overrides RunLeadershipTermAsync without calling base and allows an OCE to
                // propagate will silently bypass this catch on simultaneous cancellation. BackgroundService
                // swallows the exception, but the "{ServiceName}: exiting (stopping)" log is never emitted.
                // Consider broadening the filter to: when (ct.IsCancellationRequested)
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
}
