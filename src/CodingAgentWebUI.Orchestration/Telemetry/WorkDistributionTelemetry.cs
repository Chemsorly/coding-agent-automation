using System.Diagnostics.Metrics;
using CodingAgentWebUI.Pipeline.Models;

namespace CodingAgentWebUI.Orchestration.Telemetry;

/// <summary>
/// Dedicated meter and instruments for 035a work distribution metrics.
/// Registered via <c>.AddMeter("CodingAgent.WorkDistribution")</c> in OTel config.
/// </summary>
public static class WorkDistributionTelemetry
{
    public const string MeterName = "CodingAgent.WorkDistribution";

    public static readonly Meter Meter = new(MeterName);

    /// <summary>
    /// Histogram: time from WorkItem creation (Pending) to Dispatched.
    /// </summary>
    public static readonly Histogram<double> DispatchLatency =
        Meter.CreateHistogram<double>("workdistribution.dispatch_latency_seconds", "s",
            "Time from work item creation to dispatch");

    /// <summary>
    /// Histogram: time spent in Pending status before being dispatched.
    /// </summary>
    public static readonly Histogram<double> PendingDuration =
        Meter.CreateHistogram<double>("workdistribution.workitems_pending_duration_seconds", "s",
            "Duration work items spend in Pending status");

    /// <summary>
    /// Histogram: total execution duration of dispatched jobs (Dispatched → terminal).
    /// </summary>
    public static readonly Histogram<double> JobExecutionDuration =
        Meter.CreateHistogram<double>("workdistribution.job_execution_duration_seconds", "s",
            "Total execution duration of dispatched jobs");

    /// <summary>
    /// Gauge: epoch seconds of the last DispatchService poll cycle.
    /// Used for alerting on silent dispatch failures (stale poll = dispatch starvation).
    /// </summary>
    public static readonly ObservableGauge<double> DispatcherLastPollEpoch;

    /// <summary>
    /// Gauge: number of available credential PVCs in the kiro pool.
    /// </summary>
    public static readonly ObservableGauge<int> CredentialPoolAvailable;

    /// <summary>
    /// Gauge: number of claimed credential PVCs in the kiro pool.
    /// </summary>
    public static readonly ObservableGauge<int> CredentialPoolClaimed;

    /// <summary>
    /// Counter: work items transitioned to terminal states.
    /// Tags: status (succeeded/failed/cancelled), failure_reason.
    /// </summary>
    public static readonly Counter<long> WorkItemsTerminated =
        Meter.CreateCounter<long>("workdistribution.workitems_terminated", "{item}",
            "Work items reaching terminal status");

    /// <summary>
    /// Counter: number of dispatch poll cycles executed.
    /// </summary>
    public static readonly Counter<long> DispatcherPollCount =
        Meter.CreateCounter<long>("workdistribution.dispatcher_polls", "{poll}",
            "Number of dispatch poll cycles executed");

    // ── Observable gauge backing state ──────────────────────────────────────

    private static double _lastPollEpochSeconds;
    private static int _credentialPoolAvailable;
    private static int _credentialPoolClaimed;
    private static Func<IEnumerable<Measurement<long>>>? _workItemsByStatusCallback;

    static WorkDistributionTelemetry()
    {
        DispatcherLastPollEpoch = Meter.CreateObservableGauge(
            "workdistribution.dispatcher_last_poll_epoch_seconds",
            observeValue: () => _lastPollEpochSeconds,
            unit: "s",
            description: "Epoch seconds of the last DispatchService poll cycle");

        CredentialPoolAvailable = Meter.CreateObservableGauge(
            "workdistribution.credential_pool_available",
            observeValue: () => new Measurement<int>(_credentialPoolAvailable,
                new KeyValuePair<string, object?>("pool", "kiro")),
            unit: "{pvc}",
            description: "Number of available credential PVCs");

        CredentialPoolClaimed = Meter.CreateObservableGauge(
            "workdistribution.credential_pool_claimed",
            observeValue: () => new Measurement<int>(_credentialPoolClaimed,
                new KeyValuePair<string, object?>("pool", "kiro")),
            unit: "{pvc}",
            description: "Number of claimed credential PVCs");

        Meter.CreateObservableGauge(
            "workdistribution.workitems_by_status",
            observeValues: () => _workItemsByStatusCallback?.Invoke()
                ?? Enumerable.Empty<Measurement<long>>(),
            unit: "{item}",
            description: "Count of work items by status and agent_selector");
    }

    /// <summary>
    /// Records the current epoch time as the last poll timestamp.
    /// Called by DispatchService after each poll cycle.
    /// </summary>
    public static void RecordLastPollEpoch()
    {
        _lastPollEpochSeconds = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;
    }

    /// <summary>
    /// Updates credential pool gauge values.
    /// Called by DispatchService after computing PVC availability.
    /// </summary>
    public static void UpdateCredentialPoolMetrics(int available, int claimed)
    {
        _credentialPoolAvailable = available;
        _credentialPoolClaimed = claimed;
    }

    /// <summary>
    /// Registers a callback that supplies workitems_by_status measurements.
    /// Called once at startup from DI registration when DB is configured.
    /// The callback should query WorkItems grouped by (Status, AgentSelector).
    /// </summary>
    public static void RegisterWorkItemsByStatusCallback(Func<IEnumerable<Measurement<long>>> callback)
    {
        _workItemsByStatusCallback = callback;
    }

    /// <summary>
    /// Emits a structured Information-level log for terminal work item transitions.
    /// Satisfies Requirement 10.3: workItemId, status, duration, agentId, failureReason.
    /// </summary>
    public static void LogTerminalStatus(
        Guid workItemId,
        WorkItemStatus status,
        TimeSpan? duration,
        string? agentId,
        FailureReason? failureReason)
    {
        Serilog.Log.Information(
            "WorkItem terminal: {WorkItemId} → {Status}, duration={DurationSeconds:F1}s, agent={AgentId}, reason={FailureReason}",
            workItemId,
            status,
            duration?.TotalSeconds ?? -1,
            agentId ?? "unknown",
            failureReason?.ToString() ?? "none");

        WorkItemsTerminated.Add(1,
            new KeyValuePair<string, object?>("status", status.ToString()),
            new KeyValuePair<string, object?>("failure_reason", failureReason?.ToString() ?? "none"));

        // Record job execution duration (Dispatched → terminal)
        if (duration.HasValue && duration.Value.TotalSeconds >= 0)
        {
            JobExecutionDuration.Record(duration.Value.TotalSeconds,
                new KeyValuePair<string, object?>("status", status.ToString()));
        }
    }
}


