using System.Diagnostics.Metrics;
using CodingAgentWebUI.Pipeline.Models;

namespace CodingAgentWebUI.Pipeline.Telemetry;

/// <summary>
/// Dedicated meter and instruments for work distribution metrics.
/// Registered via <c>.AddMeter("CodingAgent.WorkDistribution")</c> in OTel config.
/// Defined in <c>CodingAgentWebUI.Pipeline</c> so all deployable processes (API,
/// Job Controller, monolith) can reference it without taking a dependency on
/// <c>CodingAgentWebUI.Orchestration</c>.
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
            "Time from work item creation to dispatch",
            advice: new InstrumentAdvice<double>
            {
                HistogramBucketBoundaries = [5, 10, 30, 60, 120, 300, 600, 900, 1800, 3600]
            });

    /// <summary>
    /// Histogram: time spent in Pending status before being dispatched.
    /// </summary>
    public static readonly Histogram<double> PendingDuration =
        Meter.CreateHistogram<double>("workdistribution.workitems_pending_duration_seconds", "s",
            "Duration work items spend in Pending status",
            advice: new InstrumentAdvice<double>
            {
                HistogramBucketBoundaries = [5, 10, 30, 60, 120, 300, 600, 900, 1800, 3600]
            });

    /// <summary>
    /// Histogram: total execution duration of dispatched jobs (Dispatched → terminal).
    /// Matches PipelineTelemetry.JobDuration buckets — same underlying job lifecycle.
    /// </summary>
    public static readonly Histogram<double> JobExecutionDuration =
        Meter.CreateHistogram<double>("workdistribution.job_execution_duration_seconds", "s",
            "Total execution duration of dispatched jobs",
            advice: new InstrumentAdvice<double>
            {
                HistogramBucketBoundaries = [30, 60, 120, 300, 600, 900, 1200, 1800, 2700, 3600, 5400, 7200, 10800, 14400, 18000, 21600]
            });

    /// <summary>
    /// Histogram: execution age (seconds since dispatch) at the moment a timeout is enforced.
    /// Used as a canary: if values cluster near zero, the timeout anchor is wrong.
    /// Alert rule: p10 &lt; configured timeout → indicates a timestamp bug.
    /// </summary>
    public static readonly Histogram<double> TimeoutExecutionAge =
        Meter.CreateHistogram<double>("workdistribution.timeout_execution_age_seconds", "s",
            "Execution age at timeout enforcement — canary for anchor correctness",
            advice: new InstrumentAdvice<double>
            {
                HistogramBucketBoundaries = [30, 60, 120, 300, 600, 900, 1200, 1800, 2700, 3600, 5400, 7200, 10800, 14400, 18000, 21600]
            });

    /// <summary>
    /// Counter: timeout enforcement skipped due to canary invariant violation.
    /// Any non-zero value indicates a bug in timestamp handling.
    /// </summary>
    public static readonly Counter<long> TimeoutCanaryViolations =
        Meter.CreateCounter<long>("workdistribution.timeout_canary_violations", "{violation}",
            "Timeout enforcement blocked by canary invariant — indicates timestamp bug");

    /// <summary>
    /// Counter: failed attempts to persist LastProgressAt to the DB.
    /// Sustained non-zero rate indicates progress tracking degradation — agents may be
    /// falsely timed out because ReconciliationService sees stale LastProgressAt values.
    /// </summary>
    public static readonly Counter<long> ProgressWriteFailures =
        Meter.CreateCounter<long>("workdistribution.progress_write_failures", "{failure}",
            "Failed LastProgressAt DB writes — sustained failures risk false-positive timeouts");

    /// <summary>
    /// Gauge: epoch seconds of the last DispatchService poll cycle.
    /// Used for alerting on silent dispatch failures (stale poll = dispatch starvation).
    /// Emits no measurement when <see cref="RecordLastPollEpoch"/> has never been called
    /// (i.e. this process is not a dispatcher). This prevents the API and Orchestrator from
    /// permanently exporting 0, which would fire the DispatcherStalled alert from boot.
    /// </summary>
    public static readonly ObservableGauge<double> DispatcherLastPollEpoch =
        Meter.CreateObservableGauge<double>(
            "workdistribution.dispatcher_last_poll_epoch_seconds",
            observeValues: () => _pollEpochRecorded
                ? [new Measurement<double>(_lastPollEpochSeconds)]
                : [],
            unit: "s",
            description: "Epoch seconds of the last DispatchService poll cycle");

    /// <summary>
    /// Gauge: number of available credential PVCs in the kiro pool.
    /// </summary>
    public static readonly ObservableGauge<int> CredentialPoolAvailable =
        Meter.CreateObservableGauge(
            "workdistribution.credential_pool_available",
            observeValue: () => new Measurement<int>(_credentialPoolAvailable,
                new KeyValuePair<string, object?>("pool", "kiro")),
            unit: "{pvc}",
            description: "Number of available credential PVCs");

    /// <summary>
    /// Gauge: number of claimed credential PVCs in the kiro pool.
    /// </summary>
    public static readonly ObservableGauge<int> CredentialPoolClaimed =
        Meter.CreateObservableGauge(
            "workdistribution.credential_pool_claimed",
            observeValue: () => new Measurement<int>(_credentialPoolClaimed,
                new KeyValuePair<string, object?>("pool", "kiro")),
            unit: "{pvc}",
            description: "Number of claimed credential PVCs");

    /// <summary>
    /// Counter: work items transitioned to terminal states.
    /// Tags: status (succeeded/failed/cancelled), failure_reason.
    /// </summary>
    public static readonly Counter<long> WorkItemsTerminated =
        Meter.CreateCounter<long>("workdistribution.workitems_terminated", "{item}",
            "Work items reaching terminal status");

    /// <summary>
    /// Counter: agent jobs killed by the session timeout enforcer.
    /// Each increment corresponds to one work item transitioned to Failed with FailureReason=Timeout.
    /// Tags: agent_selector.
    /// Alert rule: rate > 2 / 7d → investigate timeout headroom.
    /// </summary>
    public static readonly Counter<long> AgentTimeouts =
        Meter.CreateCounter<long>("workdistribution.agent_timeouts", "{job}",
            "Agent jobs killed by session timeout enforcer");

    /// <summary>
    /// Counter: PVC pool exhaustion events.
    /// Fires once per work item that attempted claim and found no available PVC.
    /// Tags: pool (always "kiro" currently).
    /// Alert rule: any non-zero rate when pool is at full concurrency.
    /// </summary>
    public static readonly Counter<long> PvcPoolExhaustions =
        Meter.CreateCounter<long>("workdistribution.pvc_pool_exhaustions", "{event}",
            "PVC pool exhaustion events — no available PVC when a job attempted to claim one");

    /// <summary>
    /// Counter: number of dispatch poll cycles executed.
    /// </summary>
    public static readonly Counter<long> DispatcherPollCount =
        Meter.CreateCounter<long>("workdistribution.dispatcher_polls", "{poll}",
            "Number of dispatch poll cycles executed");

    /// <summary>
    /// Counter: number of <c>PipelineRuns</c> rows deleted by the per-project retention sweep.
    /// </summary>
    public static readonly Counter<long> DbRetentionPipelineRunsDeleted =
        Meter.CreateCounter<long>(
            "pipeline.db_retention.pipeline_runs_deleted",
            unit: "{row}",
            description: "Number of PipelineRuns rows deleted by the retention sweep.");

    /// <summary>
    /// Counter: number of <c>WorkItems</c> rows deleted by the per-project retention sweep.
    /// </summary>
    public static readonly Counter<long> DbRetentionWorkItemsDeleted =
        Meter.CreateCounter<long>(
            "pipeline.db_retention.work_items_deleted",
            unit: "{row}",
            description: "Number of WorkItems rows deleted by the retention sweep.");

    // ── Observable gauge backing state ──────────────────────────────────────

    private static double _lastPollEpochSeconds;
    private static bool _pollEpochRecorded;  // explicit init flag — avoids magic zero sentinel
    private static int _credentialPoolAvailable;
    private static int _credentialPoolClaimed;
    private static Func<IEnumerable<Measurement<long>>>? _workItemsByStatusCallback;

    static WorkDistributionTelemetry()
    {
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
        _pollEpochRecorded = true;
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
    /// Records the dispatch latency and pending duration metrics for a work item.
    /// Called by all dispatch paths after a work item transitions to Dispatched.
    /// </summary>
    /// <param name="dispatchedAt">
    /// The timestamp at which the work item was dispatched.
    /// </param>
    /// <param name="originalEnqueuedAt">
    /// The original enqueue time if re-dispatched; used as the latency anchor when set.
    /// </param>
    /// <param name="createdAt">
    /// The work item creation timestamp. Used as the latency anchor when
    /// <paramref name="originalEnqueuedAt"/> is null.
    /// </param>
    /// <param name="agentSelector">
    /// The agent selector label. Null is coalesced to empty string to avoid a null OTel tag value.
    /// </param>
    public static void RecordDispatchLatency(
        DateTimeOffset dispatchedAt,
        DateTimeOffset? originalEnqueuedAt,
        DateTimeOffset createdAt,
        string? agentSelector)
    {
        var latency = (dispatchedAt - (originalEnqueuedAt ?? createdAt)).TotalSeconds;
        var tag = new KeyValuePair<string, object?>("agent_selector", agentSelector ?? "");
        DispatchLatency.Record(latency, tag);
        PendingDuration.Record(latency, tag);
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
