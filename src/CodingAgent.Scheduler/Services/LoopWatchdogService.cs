using System.Text.Json;
using CodingAgent.Api.Client;
using CodingAgent.Pipeline;
using CodingAgent.Pipeline.Interfaces;
using Microsoft.Extensions.Hosting;
using ILogger = Serilog.ILogger;

namespace CodingAgent.Scheduler.Services;

/// <summary>
/// Makes the pipeline loop on the leader match what users asked for, and reports its status (spec 051 D11).
/// </summary>
/// <remarks>
/// <para>
/// Loop control is desired state: Start and Stop set <c>ClosedLoopAutoStart</c> in the pipeline config,
/// and Resume sets <c>LoopResumeRequestedAt</c>. No pod receives a command, so none can reach the wrong
/// pod. On the leader (or with no leader gate, in single-replica and dev mode), every tick:
/// </para>
/// <list type="number">
///   <item>resumes the loop when <c>LoopResumeRequestedAt</c> is newer than the last request handled
///         (the first value seen is only noted: a request older than this pod's leadership is stale);</item>
///   <item>starts the loop when it should run and does not. This also self-heals a loop an unhandled
///         exception stopped, which <c>/healthz</c> cannot see. A refused start leaves its reasons in
///         the loop's validation errors, which the status carries;</item>
///   <item>stops the loop when it runs and should not;</item>
///   <item>pushes the loop status to the API when it changed, and at least every
///         <see cref="StatusRefreshInterval"/> so the API can tell a silent Scheduler from an idle one.</item>
/// </list>
/// <para>
/// A circuit-breaker pause keeps <see cref="IPipelineLoopService.IsLoopActive"/> true, so the
/// start rule does not fire during one.
/// </para>
/// </remarks>
public sealed class LoopWatchdogService : BackgroundService
{
    private readonly IPipelineLoopService _loopService;
    private readonly IPipelineApiConfigClient _configClient;
    private readonly ISchedulerApiClient _statusClient;
    private readonly ILeaderGate? _leaderGate;
    private readonly ILogger _logger;
    private readonly TimeSpan _interval;
    private readonly TimeProvider _time;

    private bool _resumeBaselineTaken;
    private DateTimeOffset? _handledResumeRequest;
    private string? _pushedStatus;
    private DateTimeOffset _pushedAt;
    private bool _reconcileFailing;
    private bool _startFailing;
    private bool _pushFailing;

    /// <summary>Default check interval: Start, Stop and Resume take effect within this time.</summary>
    public static readonly TimeSpan DefaultInterval = TimeSpan.FromSeconds(5);

    /// <summary>How often an unchanged status is pushed again.</summary>
    public static readonly TimeSpan StatusRefreshInterval = TimeSpan.FromSeconds(30);

    /// <param name="loopService">The pipeline loop service to reconcile.</param>
    /// <param name="configClient">Client for reading the desired loop state from the API.</param>
    /// <param name="statusClient">Client for pushing the loop status to the API.</param>
    /// <param name="leaderGate">
    /// Leader election gate. Pass <c>null</c> in single-replica / dev mode
    /// (no K8s leader election) — this pod then acts as the leader.
    /// </param>
    /// <param name="logger">Serilog logger.</param>
    /// <param name="interval">How often to check; <see cref="DefaultInterval"/> when <c>null</c>.</param>
    /// <param name="time">Clock for the status refresh; the system clock when <c>null</c>.</param>
    public LoopWatchdogService(
        IPipelineLoopService loopService,
        IPipelineApiConfigClient configClient,
        ISchedulerApiClient statusClient,
        ILeaderGate? leaderGate,
        ILogger logger,
        TimeSpan? interval = null,
        TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(loopService);
        ArgumentNullException.ThrowIfNull(configClient);
        ArgumentNullException.ThrowIfNull(statusClient);
        ArgumentNullException.ThrowIfNull(logger);
        _loopService = loopService;
        _configClient = configClient;
        _statusClient = statusClient;
        _leaderGate = leaderGate;
        _logger = logger.ForContext<LoopWatchdogService>();
        _interval = interval ?? DefaultInterval;
        _time = time ?? TimeProvider.System;
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.Information("LoopWatchdogService started — checking every {Interval}", _interval);

        using var timer = new PeriodicTimer(_interval);
        do
        {
            await TickAsync(stoppingToken);
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    /// <summary>
    /// One check: on the leader, reconciles the loop with the desired state and pushes its status.
    /// Never throws except on cancellation, so a failing API does not stop the watchdog.
    /// </summary>
    internal async Task TickAsync(CancellationToken ct)
    {
        if (_leaderGate is { IsLeader: false })
        {
            // Push at once if this pod becomes the leader.
            _pushedStatus = null;
            return;
        }

        await ReconcileAsync(ct);
        await PushStatusAsync(ct);
    }

    private async Task ReconcileAsync(CancellationToken ct)
    {
        try
        {
            var config = await _configClient.GetPipelineConfigAsync(ct);
            _reconcileFailing = false;

            ApplyResumeRequest(config.LoopResumeRequestedAt);

            if (config.ClosedLoopAutoStart && !_loopService.IsLoopActive)
                await StartAsync();
            else if (!config.ClosedLoopAutoStart && _loopService.IsLoopActive)
                _loopService.StopLoop();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Host is stopping — exit silently.
        }
        catch (Exception ex)
        {
            if (!_reconcileFailing)
                _logger.Warning(ex, "LoopWatchdogService: could not reconcile the loop — will retry every {Interval}", _interval);
            _reconcileFailing = true;
        }
    }

    private void ApplyResumeRequest(DateTimeOffset? requestedAt)
    {
        if (!_resumeBaselineTaken)
        {
            _resumeBaselineTaken = true;
            _handledResumeRequest = requestedAt;
            return;
        }
        if (requestedAt is null || requestedAt <= _handledResumeRequest)
            return;

        _handledResumeRequest = requestedAt;
        _logger.Information("LoopWatchdogService: resume requested at {RequestedAt} — resuming the loop", requestedAt);
        _loopService.ResumeLoop();
    }

    private async Task StartAsync()
    {
        if (await _loopService.StartLoopAsync())
        {
            _startFailing = false;
            _logger.Information("LoopWatchdogService: loop started");
            return;
        }
        if (!_startFailing)
        {
            _logger.Warning("LoopWatchdogService: the loop should run but did not start: {Errors}",
                string.Join(" ", _loopService.ValidationErrors));
        }
        _startFailing = true;
    }

    /// <summary>The loop's state as the status the API serves to the UI.</summary>
    internal static LoopStatusDto BuildStatus(IPipelineLoopService loop) => new(
        loop.IsLoopActive,
        loop.StatusMessage,
        loop.CurrentIssueIdentifier,
        loop.ProcessedCount,
        loop.FailedCount,
        loop.QueueCount,
        loop.IsCircuitBroken,
        loop.LastPollError,
        loop.CurrentCycleTemplateIndex,
        loop.CurrentCycleTemplateCount,
        loop.ValidationErrors,
        loop.TemplateStatuses);

    private async Task PushStatusAsync(CancellationToken ct)
    {
        var status = BuildStatus(_loopService);
        var serialized = JsonSerializer.Serialize(status, PipelineJsonOptions.Default);
        var now = _time.GetUtcNow();
        if (serialized == _pushedStatus && now - _pushedAt < StatusRefreshInterval)
            return;

        try
        {
            await _statusClient.ReportLoopStatusAsync(status, ct);
            _pushedStatus = serialized;
            _pushedAt = now;
            _pushFailing = false;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Host is stopping — exit silently.
        }
        catch (Exception ex)
        {
            if (!_pushFailing)
                _logger.Warning(ex, "LoopWatchdogService: could not push the loop status to the API — will retry every {Interval}", _interval);
            _pushFailing = true;
        }
    }
}
