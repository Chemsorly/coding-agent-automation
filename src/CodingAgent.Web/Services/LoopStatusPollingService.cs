using CodingAgent.Api.Client;
using CodingAgent.Pipeline.Models;
using Microsoft.Extensions.Hosting;
using ILogger = Serilog.ILogger;

namespace CodingAgent.Web.Services;

/// <summary>
/// <see cref="ILoopStatusService"/> implementation that polls GET /loop/status on the
/// Scheduler every <see cref="DefaultInterval"/> (configurable via
/// SchedulerApi:StatusPollIntervalSeconds). Uses <see cref="PeriodicTimer"/> instead of
/// Timer+async void to prevent subscriber exceptions from crashing the process.
///
/// On poll failure: sets <see cref="IsSchedulerUnreachable"/> = true and preserves prior state.
/// On recovery: clears the flag.
///
/// <see cref="OnChange"/> fires only when the polled status or <see cref="IsSchedulerUnreachable"/>
/// actually changes. Subscribers re-render whole pages, so a tick that changed nothing must not
/// reach them.
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

        while (await WaitForNextPollAsync(timer, stoppingToken))
        {
            bool changed;
            try
            {
                var dto = await _schedulerClient.GetLoopStatusAsync(stoppingToken);
                changed = _isSchedulerUnreachable || !SameStatus(_status, dto);
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
                changed = !_isSchedulerUnreachable;
                _isSchedulerUnreachable = true;
                // Preserve _status — do not reset to defaults on transient failure
            }

            if (!changed)
                continue;

            NotifySubscribers();
        }
    }

    // Returns false when the timer is disposed or the wait is cancelled — both end the polling loop.
    private static async Task<bool> WaitForNextPollAsync(PeriodicTimer timer, CancellationToken stoppingToken)
    {
        try
        {
            return await timer.WaitForNextTickAsync(stoppingToken);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    // Fire OnChange to each subscriber independently so that a throw from one
    // subscriber does not skip the remaining ones.
    private void NotifySubscribers()
    {
        var handler = OnChange;
        if (handler is null)
            return;

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

    private static readonly IReadOnlyList<string> NoErrors = [];
    private static readonly IReadOnlyDictionary<string, ConfigStatusSnapshot> NoTemplateStatuses =
        new Dictionary<string, ConfigStatusSnapshot>();

    /// <summary>
    /// Compares two status snapshots by value. <see cref="LoopStatusDto"/> is a record, but its
    /// generated equality compares the list and dictionary members by reference, and every poll
    /// deserializes new ones. So the scalar members are compared through the record's own equality
    /// (with the two collections swapped for shared empties, so a scalar added to the DTO later is
    /// covered without touching this method) and the collections by content.
    /// </summary>
    private static bool SameStatus(LoopStatusDto a, LoopStatusDto b)
    {
        if (a with { ValidationErrors = NoErrors, TemplateStatuses = NoTemplateStatuses }
            != b with { ValidationErrors = NoErrors, TemplateStatuses = NoTemplateStatuses })
            return false;

        if (!(a.ValidationErrors ?? NoErrors).SequenceEqual(b.ValidationErrors ?? NoErrors))
            return false;

        // ConfigStatusSnapshot is a record of scalars, so Equals compares it by value.
        var aStatuses = a.TemplateStatuses ?? NoTemplateStatuses;
        var bStatuses = b.TemplateStatuses ?? NoTemplateStatuses;
        return aStatuses.Count == bStatuses.Count
            && aStatuses.All(kv => bStatuses.TryGetValue(kv.Key, out var other) && Equals(kv.Value, other));
    }
}
