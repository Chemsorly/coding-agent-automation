using CodingAgent.Api.Client;
using CodingAgent.Pipeline.Interfaces;
using Microsoft.Extensions.Hosting;
using Serilog;
using ILogger = Serilog.ILogger;

namespace CodingAgent.Scheduler.Services;

/// <summary>
/// Watchdog that self-heals a dormant-but-leader pipeline loop.
/// </summary>
/// <remarks>
/// <para>
/// When <see cref="PipelineLoopService"/> is permanently stopped by an unhandled exception
/// (the "dormant-but-leader" scenario), Kubernetes never evicts the pod because
/// <c>/healthz</c> always returns 200. This service fills that gap by polling
/// <see cref="IPipelineLoopService.IsLoopActive"/> on a configurable interval and
/// restarting the loop when the conditions for self-heal are met:
/// </para>
/// <list type="number">
///   <item>This pod is the leader (<see cref="ILeaderGate.IsLeader"/> is <c>true</c>
///         or the gate is <c>null</c> — single-replica / dev mode).</item>
///   <item>The loop is not running (<see cref="IPipelineLoopService.IsLoopActive"/> is <c>false</c>).</item>
///   <item>The operator has not deliberately stopped the loop
///         (<c>ClosedLoopAutoStart</c> is <c>true</c> in the persisted config).</item>
/// </list>
/// <para>
/// Note on circuit-breaker interaction: when the circuit breaker is open,
/// <see cref="IPipelineLoopService.IsLoopActive"/> is <c>true</c> (the loop is
/// paused, not stopped). Condition 2 therefore already prevents the watchdog from
/// firing during a circuit-breaker pause — no additional guard is needed.
/// </para>
/// <para>
/// Note on null gate: a null <see cref="ILeaderGate"/> means leader election is
/// not configured (single-replica / dev mode). In that case the watchdog treats
/// the current instance as the leader and heals unconditionally if conditions 2
/// and 3 are met — matching the pattern used by <see cref="WorkItemCountsPoller"/>.
/// </para>
/// </remarks>
public sealed class LoopWatchdogService : BackgroundService
{
    private readonly IPipelineLoopService _loopService;
    private readonly IPipelineApiConfigClient _configClient;
    private readonly ILeaderGate? _leaderGate;
    private readonly ILogger _logger;
    private readonly TimeSpan _interval;

    /// <summary>Default watchdog check interval (2 minutes).</summary>
    public static readonly TimeSpan DefaultInterval = TimeSpan.FromMinutes(2);

    /// <param name="loopService">The pipeline loop service to supervise.</param>
    /// <param name="configClient">Client for reading <c>ClosedLoopAutoStart</c> from the API.</param>
    /// <param name="leaderGate">
    /// Leader election gate. Pass <c>null</c> in single-replica / dev mode
    /// (no K8s leader election) — the watchdog will heal unconditionally.
    /// </param>
    /// <param name="logger">Serilog logger.</param>
    /// <param name="interval">
    /// How often to check the loop state. Pass a small value in tests to make the
    /// timer fire quickly without real wall-clock waiting. Defaults to
    /// <see cref="DefaultInterval"/> (2 minutes) when <c>null</c>.
    /// </param>
    public LoopWatchdogService(
        IPipelineLoopService loopService,
        IPipelineApiConfigClient configClient,
        ILeaderGate? leaderGate,
        ILogger logger,
        TimeSpan? interval = null)
    {
        ArgumentNullException.ThrowIfNull(loopService);
        ArgumentNullException.ThrowIfNull(configClient);
        ArgumentNullException.ThrowIfNull(logger);
        _loopService = loopService;
        _configClient = configClient;
        _leaderGate = leaderGate;
        _logger = logger.ForContext<LoopWatchdogService>();
        _interval = interval ?? DefaultInterval;
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.Information("LoopWatchdogService started — checking every {Interval}", _interval);

        using var timer = new PeriodicTimer(_interval);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            await CheckAndHealAsync(stoppingToken);
        }
    }

    /// <summary>
    /// Checks whether the loop needs healing and calls <see cref="IPipelineLoopService.StartLoopAsync"/>
    /// if the self-heal conditions are met. Swallows all exceptions so a transient API failure
    /// during config fetch does not crash the watchdog.
    /// </summary>
    internal async Task CheckAndHealAsync(CancellationToken ct)
    {
        try
        {
            // Condition 1: this pod must be the leader (null gate = dev mode = treat as leader).
            if (_leaderGate is { IsLeader: false })
                return;

            // Condition 2: the loop must be dormant.
            if (_loopService.IsLoopActive)
                return;
            // TODO: the IsLoopActive read above and the StartLoopAsync() call below are not atomic.
            //   Between the read and the call, another concurrent watchdog tick or external UI call
            //   could start the loop, making the call here redundant. This is safe only because
            //   StartLoopAsync() re-checks IsLoopActive under its own internal lock and returns false
            //   when already active — making a redundant call a benign no-op. If StartLoopAsync ever
            //   loses its internal IsLoopActive guard, this watchdog could race to double-start the
            //   loop (resetting counters, replacing _loopCts). Not a defect today.

            // Condition 3: the operator has not deliberately stopped the loop.
            // Read from the API on every check so a config change takes effect within one interval.
            var config = await _configClient.GetPipelineConfigAsync(ct);
            if (!config.ClosedLoopAutoStart)
            {
                _logger.Debug(
                    "LoopWatchdogService: loop is dormant but ClosedLoopAutoStart=false — not healing (deliberate stop)");
                return;
            }

            // All conditions met — restart the loop.
            _logger.Warning(
                "LoopWatchdogService: loop is dormant and this pod is the leader — calling StartLoopAsync() to self-heal");

            var started = await _loopService.StartLoopAsync();
            if (started)
                _logger.Information("LoopWatchdogService: loop successfully restarted");
            else
                _logger.Warning(
                    "LoopWatchdogService: StartLoopAsync() returned false — loop could not be started (no valid templates?)");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Host is stopping — exit silently.
        }
        catch (Exception ex)
        {
            // Swallow all other exceptions: a transient config-fetch failure must not stop the watchdog.
            _logger.Warning(ex, "LoopWatchdogService: error during watchdog check — will retry next interval");
        }
    }
}
