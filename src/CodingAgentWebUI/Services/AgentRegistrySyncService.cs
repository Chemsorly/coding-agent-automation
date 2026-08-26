using ILogger = Serilog.ILogger;

namespace CodingAgentWebUI.Services;

/// <summary>
/// Background poller that keeps <see cref="ApiAgentRegistryService"/> current by fetching
/// <c>GET /api/agents</c> from the Pipeline API on a fixed interval.
///
/// <para>
/// This exists because <c>IAgentRegistryService</c> is synchronous and its callers sit on Blazor
/// render paths. Moving the fetch here is what lets the registry answer reads from memory instead
/// of blocking on HTTP, without any sync-over-async in the request path.
/// </para>
/// </summary>
public sealed class AgentRegistrySyncService : BackgroundService
{
    private readonly ApiAgentRegistryService _registry;
    private readonly TimeProvider _clock;
    private readonly ILogger _logger;

    /// <summary>
    /// How often to re-fetch.
    ///
    /// <para>
    /// Deliberately faster than the agent monitoring page's own 5s redraw, so an agent that connects
    /// or drops is reflected within a single refresh rather than lagging a cycle behind it. The cost
    /// is trivial — <c>GET /api/agents</c> is an in-memory read on the API — and the interval sits
    /// far enough under <see cref="ApiAgentRegistryService.MaxSnapshotAge"/> that a run of failed
    /// polls is absorbed before the agent list blanks.
    /// </para>
    /// </summary>
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromSeconds(2);

    public AgentRegistrySyncService(ApiAgentRegistryService registry, TimeProvider clock, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(logger);

        _registry = registry;
        _clock = clock;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(PollInterval, _clock);

        // Consecutive failures, so a Pipeline API that stays down produces one warning and then
        // debug-level noise rather than a warning every PollInterval for as long as the outage lasts.
        var consecutiveFailures = 0;

        do
        {
            try
            {
                await _registry.RefreshAsync(stoppingToken);

                if (consecutiveFailures > 0)
                {
                    _logger.Information(
                        "Agent registry sync recovered after {FailureCount} consecutive failure(s); " +
                        "{AgentCount} agent(s) visible.",
                        consecutiveFailures, _registry.GetAllAgents().Count);
                    consecutiveFailures = 0;
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;  // shutdown, not a failure
            }
            // Broad by design: a poller must survive any fetch failure — HTTP, DNS, TLS,
            // deserialization — and retry on the next tick rather than terminating the loop.
            catch (Exception ex)
            {
                consecutiveFailures++;
                if (consecutiveFailures == 1)
                {
                    _logger.Warning(ex,
                        "Agent registry sync failed. The agent list will be served from the last " +
                        "snapshot until it exceeds MaxSnapshotAge, then reported as empty.");
                }
                else
                {
                    _logger.Debug(ex,
                        "Agent registry sync failed ({FailureCount} consecutive).", consecutiveFailures);
                }
            }
        }
        while (await SafeWaitForNextTickAsync(timer, stoppingToken));
    }

    /// <summary>
    /// <see cref="PeriodicTimer.WaitForNextTickAsync"/> throws on cancellation; shutdown is an
    /// ordinary stop for this loop, so translate it into a clean exit.
    /// </summary>
    private static async ValueTask<bool> SafeWaitForNextTickAsync(PeriodicTimer timer, CancellationToken ct)
    {
        try
        {
            return await timer.WaitForNextTickAsync(ct);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }
}
