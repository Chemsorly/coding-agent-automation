using System.Net.Http;
using System.Threading.RateLimiting;
using Microsoft.Extensions.Hosting;
using Polly.CircuitBreaker;
using Serilog;

namespace CodingAgent.Pipeline.LeaderElection;

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
    private readonly ILogger _log;

    /// <summary>
    /// The leader election service used to determine if this instance holds the leader lease.
    /// </summary>
    protected ILeaderElectionService LeaderElection { get; }

    /// <summary>
    /// The rate limiter created from <c>rateLimitPerSecond</c> passed to the constructor,
    /// or <c>null</c> if the subclass did not request one. Subclasses that require a rate
    /// limiter enforce non-null at call sites via <c>?? throw new InvalidOperationException(...)</c>.
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
    /// <param name="logger">
    /// Optional logger. When provided, used directly for all log output from this instance.
    /// When omitted, falls back to <c>Serilog.Log.ForContext&lt;LeaderElectedPollingService&gt;()</c>.
    /// Pass an explicit logger in tests to capture log output without touching the global static.
    /// </param>
    protected LeaderElectedPollingService(ILeaderElectionService leaderElection, int? rateLimitPerSecond = null, ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(leaderElection);
        LeaderElection = leaderElection;
        RateLimiter = rateLimitPerSecond.HasValue
            ? RateLimiterFactory.CreateTokenBucket(rateLimitPerSecond.Value)
            : null;
        _log = (logger ?? Serilog.Log.Logger).ForContext<LeaderElectedPollingService>();
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
        _log.Information("{ServiceName} started — waiting for leader election", ServiceName);

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
            catch (OperationCanceledException)
            {
                if (stoppingToken.IsCancellationRequested)
                    break; // Host stopping — exit the outer while loop cleanly
                // Otherwise: leadership lost — fall through to re-enter wait loop
            }

            if (!stoppingToken.IsCancellationRequested)
            {
                _log.Information("{ServiceName}: leadership lost, re-entering wait loop", ServiceName);
            }
        }

        _log.Information("{ServiceName}: exiting (stopping)", ServiceName);
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
        _log.Information("{ServiceName}: leader acquired, entering poll loop", ServiceName);

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
            catch (Exception ex) when (IsTransientPollingException(ex))
            {
                _log.Warning(ex, "{ServiceName}: transient error in poll cycle — will retry next interval", ServiceName);
            }
            catch (Exception ex)
            {
                _log.Error(ex, "{ServiceName}: unhandled error in poll cycle", ServiceName);
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

    /// <summary>
    /// Returns <c>true</c> if <paramref name="ex"/> is a transient infrastructure exception
    /// that is expected to resolve on the next poll cycle (e.g. a momentary network blip or
    /// circuit-breaker open state). Transient exceptions are logged at Warning level; all other
    /// exceptions are logged at Error level.
    /// </summary>
    /// <remarks>
    /// This assembly (<c>CodingAgent.Infrastructure.Common</c>) has no Npgsql or EF Core dependency,
    /// so <c>NpgsqlException.IsTransient</c> is intentionally absent here. DB-layer transient
    /// classification lives in <c>ResiliencePipelineRegistrationExtensions.IsTransientDbException</c>
    /// in the Persistence assembly. Concrete subclasses that communicate via HTTP (e.g.
    /// <see cref="ReconciliationService"/>) are fully covered by this predicate.
    /// </remarks>
    protected static bool IsTransientPollingException(Exception ex) =>
        // TODO [WARNING]: System.IO.IOException is the base class for DirectoryNotFoundException,
        // FileNotFoundException, EndOfStreamException, and other non-transient I/O errors. A
        // FileNotFoundException from application logic (e.g. missing config file) would be silently
        // downgraded to Warning and retried indefinitely. Consider narrowing to SocketException
        // (which derives from IOException and covers network-transient failures) or adding explicit
        // exclusions for non-transient IOException subclasses.
        // See Issue #2576 review findings (Correctness [WARNING]).
        ex is HttpRequestException
            or TimeoutException
            or System.IO.IOException
            or BrokenCircuitException;
}
