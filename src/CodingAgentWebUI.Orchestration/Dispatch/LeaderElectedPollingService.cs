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
/// <para>
/// Optionally manages a <see cref="TokenBucketRateLimiter"/> for subclasses that need
/// per-second dispatch rate limiting (pass <paramref name="rateLimitPerSecond"/> to opt in).
/// Services that do not need a rate limiter (e.g. <see cref="ReconciliationService"/>) omit
/// the parameter; the base class then leaves <see cref="RateLimiter"/> as <c>null</c>.
/// </para>
/// </summary>
public abstract class LeaderElectedPollingService : BackgroundService
{
    private static readonly ILogger Log = Serilog.Log.ForContext<LeaderElectedPollingService>();

    /// <summary>
    /// The leader election service used to determine if this instance holds the leader lease.
    /// </summary>
    protected ILeaderElectionService LeaderElection { get; }

    /// <summary>
    /// The rate limiter created from <c>rateLimitPerSecond</c> passed to the constructor,
    /// or <c>null</c> if the subclass did not request one.
    /// </summary>
    protected TokenBucketRateLimiter? RateLimiter { get; }

    /// <summary>
    /// Display name used in log messages. Each subclass provides its own name.
    /// </summary>
    protected abstract string ServiceName { get; }

    /// <summary>
    /// Interval in seconds between poll cycles. Each subclass provides its own value
    /// from its specific options class (e.g., DispatchServiceOptions or ReconciliationServiceOptions).
    /// </summary>
    protected abstract int PollIntervalSeconds { get; }

    /// <param name="leaderElection">Leader election service. Must not be null.</param>
    /// <param name="rateLimitPerSecond">
    /// When provided, creates a <see cref="TokenBucketRateLimiter"/> owned and disposed by this
    /// base class. Subclasses access it via <see cref="RateLimiter"/>.
    /// Omit for services that do not require rate limiting.
    /// </param>
    protected LeaderElectedPollingService(ILeaderElectionService leaderElection, int? rateLimitPerSecond = null)
    {
        // TODO: Add ArgumentNullException.ThrowIfNull(leaderElection) — see DotNetSpecialist WARNING (Issue #1912).
        LeaderElection = leaderElection;
        RateLimiter = rateLimitPerSecond.HasValue
            ? RateLimiterFactory.CreateTokenBucket(rateLimitPerSecond.Value)
            : null;
    }

    /// <inheritdoc/>
    public override void Dispose()
    {
        RateLimiter?.Dispose();
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
                // TODO: When host stop and leadership loss occur simultaneously, both ct and stoppingToken are
                // cancelled at the same time. The filter evaluates to false (!stoppingToken.IsCancellationRequested
                // is false), so the OCE propagates uncaught and BackgroundService logs it as an unhandled exception.
                // This causes a spurious error log on clean shutdown with concurrent leadership loss.
                // Consider catching the OCE unconditionally and checking stoppingToken after the catch to decide
                // whether to re-enter the wait loop or exit. See DotNetSpecialist WARNING (Issue #1912).
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
