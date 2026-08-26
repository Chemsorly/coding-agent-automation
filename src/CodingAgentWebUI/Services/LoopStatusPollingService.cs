using CodingAgentWebUI.Api.Client;
using CodingAgentWebUI.Pipeline.Models;
using Microsoft.Extensions.Hosting;
using ILogger = Serilog.ILogger;

namespace CodingAgentWebUI.Services;

/// <summary>
/// <see cref="ILoopStatusService"/> implementation that polls GET /loop/status on the
/// Scheduler every <see cref="DefaultInterval"/> (configurable via
/// SchedulerApi:StatusPollIntervalSeconds). Uses <see cref="PeriodicTimer"/> instead of
/// Timer+async void to prevent subscriber exceptions from crashing the process.
///
/// On poll failure: sets <see cref="IsSchedulerUnreachable"/> = true, preserves prior state,
/// fires <see cref="OnChange"/>. On recovery: clears the flag.
/// </summary>
public sealed class LoopStatusPollingService : BackgroundService, ILoopStatusService
{
    private static readonly TimeSpan DefaultInterval = TimeSpan.FromSeconds(3);

    private readonly ISchedulerApiClient _schedulerClient;
    private readonly ILogger _logger;
    private readonly TimeSpan _interval;

    // Snapshot updated on every successful poll — preserved on failure.
    private LoopStatusDto _status = new();
    private bool _isSchedulerUnreachable;

    public event Action? OnChange;

    public LoopStatusPollingService(
        ISchedulerApiClient schedulerClient,
        ILogger logger,
        TimeSpan? interval = null)
    {
        _schedulerClient = schedulerClient;
        _logger = logger.ForContext<LoopStatusPollingService>();
        _interval = interval ?? DefaultInterval;
    }

    // ── ILoopStatusService ────────────────────────────────────────────────────

    public bool IsLoopActive => _status.IsLoopActive;
    public string StatusMessage => _status.StatusMessage;
    public string? CurrentIssueIdentifier => _status.CurrentIssueIdentifier;
    public int ProcessedCount => _status.ProcessedCount;
    public int FailedCount => _status.FailedCount;
    public int QueueCount => _status.QueueCount;
    public bool IsCircuitBroken => _status.IsCircuitBroken;
    public string? LastPollError => _status.LastPollError;
    public int CurrentCycleTemplateIndex => _status.CurrentCycleTemplateIndex;
    public int CurrentCycleTemplateCount => _status.CurrentCycleTemplateCount;
    public IReadOnlyList<string> ValidationErrors => _status.ValidationErrors;
    public IReadOnlyDictionary<string, ConfigStatusSnapshot> TemplateStatuses => _status.TemplateStatuses;
    public bool IsSchedulerUnreachable => _isSchedulerUnreachable;

    // ── BackgroundService ─────────────────────────────────────────────────────

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.Information("LoopStatusPollingService started — polling every {Interval}", _interval);

        using var timer = new PeriodicTimer(_interval);

        while (true)
        {
            try
            {
                if (!await timer.WaitForNextTickAsync(stoppingToken))
                    break;
            }
            catch (OperationCanceledException)
            {
                break;
            }

            try
            {
                var dto = await _schedulerClient.GetLoopStatusAsync(stoppingToken);
                _status = dto;
                _isSchedulerUnreachable = false;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.Warning(ex, "LoopStatusPollingService: Scheduler unreachable — prior state preserved");
                _isSchedulerUnreachable = true;
                // Preserve _status — do not reset to defaults on transient failure
            }

            // Fire OnChange to each subscriber independently so that a throw from one
            // subscriber does not skip the remaining ones.
            var handler = OnChange;
            if (handler is not null)
            {
                foreach (var subscriber in handler.GetInvocationList())
                {
                    try { ((Action)subscriber)(); }
                    catch (Exception ex)
                    {
                        _logger.Warning(ex, "LoopStatusPollingService: OnChange subscriber {Method} threw",
                            subscriber.Method.Name);
                    }
                }
            }
        }
    }
}
