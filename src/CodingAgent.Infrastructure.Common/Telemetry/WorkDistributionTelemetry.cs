using System.Diagnostics.Metrics;
using System.Text.RegularExpressions;
using CodingAgent.Pipeline.Models;

namespace CodingAgent.Pipeline.Telemetry;

/// <summary>
/// Dedicated meter and instruments for work distribution metrics.
/// Registered via <c>.AddMeter("CodingAgent.WorkDistribution")</c> in OTel config.
/// Defined in <c>CodingAgent.Pipeline</c> so all deployable processes (API,
/// Job Controller, monolith) can reference it without taking a dependency on
/// <c>CodingAgent.Orchestration</c>.
/// </summary>
public static class WorkDistributionTelemetry
{
    public const string MeterName = "CodingAgent.WorkDistribution";

    public static readonly Meter Meter = new(MeterName);

    /// <summary>
    /// Histogram: time from WorkItem creation (Pending) to Dispatched.
    /// Buckets extended to 86400 s (24 h) in issue #2976 to prevent p95 saturation at 3600 s.
    /// </summary>
    public static readonly Histogram<double> DispatchLatency =
        Meter.CreateHistogram<double>("workdistribution.dispatch_latency_seconds", "s",
            "Time from work item creation to dispatch",
            advice: new InstrumentAdvice<double>
            {
                HistogramBucketBoundaries = [5, 10, 30, 60, 120, 300, 600, 900, 1800, 3600, 7200, 14400, 28800, 43200, 86400]
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
            observeValues: () =>
            {
                // Single volatile read — no torn-read possible between a "recorded" flag
                // and the epoch value. Zero means RecordLastPollEpoch has not been called yet.
                var ms = Volatile.Read(ref _pollEpochMillis);
                return ms > 0
                    ? [new Measurement<double>(ms / 1000.0)]
                    : [];
            },
            unit: "s",
            description: "Epoch seconds of the last DispatchService poll cycle");

    /// <summary>
    /// Gauge: number of available credential PVCs in the kiro pool.
    /// Emits no measurement until <see cref="UpdateCredentialPoolMetrics"/> has been called
    /// (the same owner-only pattern used by <see cref="DispatcherLastPollEpoch"/>).
    /// Processes that never call <see cref="UpdateCredentialPoolMetrics"/> — Scheduler,
    /// Job Controller, and Web — emit nothing, preventing spurious 0 series.
    /// Only the API process owns this metric (set in <c>WorkItemDispatchEndpoints</c>).
    /// </summary>
    public static readonly ObservableGauge<int> CredentialPoolAvailable =
        Meter.CreateObservableGauge<int>(
            "workdistribution.credential_pool_available",
            observeValues: () =>
            {
                if (!_credentialPoolUpdated) return [];
                return [new Measurement<int>(_credentialPoolAvailable,
                    new KeyValuePair<string, object?>("pool", "kiro"))];
            },
            unit: "{pvc}",
            description: "Number of available credential PVCs");

    /// <summary>
    /// Gauge: number of claimed credential PVCs in the kiro pool.
    /// Emits no measurement until <see cref="UpdateCredentialPoolMetrics"/> has been called
    /// (the same owner-only pattern used by <see cref="DispatcherLastPollEpoch"/>).
    /// </summary>
    public static readonly ObservableGauge<int> CredentialPoolClaimed =
        Meter.CreateObservableGauge<int>(
            "workdistribution.credential_pool_claimed",
            observeValues: () =>
            {
                if (!_credentialPoolUpdated) return [];
                return [new Measurement<int>(_credentialPoolClaimed,
                    new KeyValuePair<string, object?>("pool", "kiro"))];
            },
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

    /// <summary>
    /// Counter: dispatch attempts for <c>POST /api/work-items/{id}/dispatch</c>.
    /// Recorded at every outcome of <c>DispatchPendingWorkItem</c>.
    /// Tags: <c>result</c> (dispatched | deferred | transient),
    ///       <c>reason</c> (none | concurrency_limit | not_pending | no_template |
    ///                      pvc_unavailable | lock_timeout | k8s_error).
    /// Pre-initialized at API startup via <see cref="PreInitializeDispatchAttempts"/>.
    /// </summary>
    public static readonly Counter<long> DispatchAttempts =
        Meter.CreateCounter<long>("workdistribution.dispatch.attempts", "{attempt}",
            "Dispatch attempts by result and reason");

    /// <summary>
    /// Histogram: time from WorkItem dispatch to the agent's first <c>GET /assignment</c> call.
    /// Recorded once per WorkItem (on first assignment fetch) in <c>WorkItemAgentEndpoints.GetAssignment</c>.
    /// Buckets: 5, 10, 20, 30, 60, 120, 300, 600 seconds.
    /// </summary>
    public static readonly Histogram<double> PodStartSeconds =
        Meter.CreateHistogram<double>("workdistribution.pod_start_seconds", "s",
            "Time from WorkItem dispatch to first GET /assignment",
            advice: new InstrumentAdvice<double>
            {
                HistogramBucketBoundaries = [5, 10, 20, 30, 60, 120, 300, 600]
            });

    // ── Observable gauge backing state ──────────────────────────────────────

    // Single long encodes both "has been recorded" and "value":
    //   0   → RecordLastPollEpoch has never been called (no measurement emitted)
    //   > 0 → Unix epoch milliseconds of the last poll (divided by 1000.0 on read)
    // Using a single field (accessed via Volatile.Read/Write) eliminates the two-field
    // torn-read race that existed with the former (_lastPollEpochSeconds, _pollEpochRecorded) pair.
    // Note: the C# 'volatile' keyword is restricted to types ≤ 4 bytes (CS0677), so
    // Volatile.Read/Write are used instead for correct acquire/release memory ordering.
    private static long _pollEpochMillis;
    private static int _credentialPoolAvailable;
    private static int _credentialPoolClaimed;
    // Sentinel: set to true by UpdateCredentialPoolMetrics on the first call.
    // Gauges emit no measurement until the owning process (API) calls UpdateCredentialPoolMetrics,
    // preventing Scheduler, Job Controller, and Web from emitting spurious 0 series.
    private static volatile bool _credentialPoolUpdated;
    private static Func<IEnumerable<Measurement<long>>>? _workItemsByStatusCallback;

    // Backing field for the oldest-pending-age gauge.
    // 0 = no Pending items; > 0 = Unix ms of the oldest Pending WorkItem's CreatedAt.
    // A CreatedAt equal to Unix epoch (1970-01-01) would be treated as "no items" — not possible in practice.
    private static long _oldestPendingMillis;

    /// <summary>
    /// Gauge: age in seconds of the oldest Pending work item.
    /// Emits no measurement when there are no Pending items (sentinel value 0).
    /// Updated by the Scheduler's <c>WorkItemCountsService</c> every 10 s.
    /// </summary>
    public static readonly ObservableGauge<double> PendingOldestAge =
        Meter.CreateObservableGauge<double>(
            "workdistribution.pending.oldest_age_seconds",
            observeValues: () =>
            {
                var ms = Volatile.Read(ref _oldestPendingMillis);
                return ms > 0
                    ? [new Measurement<double>(
                          (DateTimeOffset.UtcNow - DateTimeOffset.FromUnixTimeMilliseconds(ms)).TotalSeconds)]
                    : [];
            },
            unit: "s",
            description: "Age of the oldest Pending work item in seconds");

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
        // Volatile.Write provides a release fence, ensuring that any preceding writes
        // are visible to other threads before they observe the new epoch value.
        // Collapses the old two-field (_lastPollEpochSeconds / _pollEpochRecorded) pattern
        // into one field to prevent the export thread from observing an inconsistent pair.
        Volatile.Write(ref _pollEpochMillis, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
    }

    /// <summary>
    /// Updates credential pool gauge values.
    /// Called by the API's <c>WorkItemDispatchEndpoints</c> after computing PVC availability.
    /// Setting these values activates the owner-only guard — only the API emits measurements
    /// for these gauges; other processes that never call this method emit nothing.
    /// </summary>
    public static void UpdateCredentialPoolMetrics(int available, int claimed)
    {
        // TODO [WARNING]: _credentialPoolAvailable and _credentialPoolClaimed are plain static int
        // fields (no volatile, no Volatile.Write). The volatile write to _credentialPoolUpdated
        // provides a release fence, but the two int stores that precede it are not guaranteed to be
        // visible to the collection thread before it sees the sentinel flip. On a CPU with a
        // store-buffer (x86 is strongly-ordered, but ARM is not), the collection thread can observe
        // _credentialPoolUpdated == true while still reading stale 0 values for the ints.
        // Fix: use Volatile.Write for both int fields (matching the _pollEpochMillis pattern), or
        // consolidate both values into an Interlocked-exchanged reference type to prevent torn reads.
        // See review findings for issue #2980.
        _credentialPoolAvailable = available;
        _credentialPoolClaimed = claimed;
        _credentialPoolUpdated = true;
    }

    /// <summary>
    /// Registers a callback that supplies workitems_by_status measurements.
    /// Called once at startup from DI registration when DB is configured.
    /// The callback should query WorkItems grouped by (Status, AgentSelector).
    /// Logs a warning if called more than once (e.g. from a misconfigured DI container
    /// or parallel test runs) — the second registration overwrites the first.
    /// </summary>
    public static void RegisterWorkItemsByStatusCallback(Func<IEnumerable<Measurement<long>>> callback)
    {
        if (Interlocked.CompareExchange(ref _workItemsByStatusCallback, callback, null) is not null)
        {
            Serilog.Log.Warning(
                "WorkDistributionTelemetry: RegisterWorkItemsByStatusCallback called more than once — " +
                "previous callback overwritten. This indicates a DI misconfiguration or parallel test run.");
            _workItemsByStatusCallback = callback;
        }
    }

    /// <summary>
    /// Records the dispatch latency metric for a work item.
    /// Called by all dispatch paths after a work item transitions to Dispatched.
    /// The duplicate <c>workitems_pending_duration_seconds</c> instrument was removed in issue #2976.
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
    }

    /// <summary>
    /// Records a dispatch attempt on <see cref="DispatchAttempts"/>.
    /// Called from <c>DispatchPendingWorkItem</c> at each outcome site.
    /// </summary>
    /// <param name="result">One of: <c>dispatched</c>, <c>deferred</c>, <c>transient</c>.</param>
    /// <param name="reason">
    /// One of: <c>none</c>, <c>not_pending</c>, <c>no_template</c>, <c>concurrency_limit</c>,
    /// <c>pvc_unavailable</c>, <c>lock_timeout</c>, <c>k8s_error</c>.
    /// </param>
    public static void RecordDispatchAttempt(string result, string reason)
    {
        DispatchAttempts.Add(1,
            new KeyValuePair<string, object?>("result", result),
            new KeyValuePair<string, object?>("reason", reason));
    }

    /// <summary>
    /// Updates the oldest-pending-age gauge backing field.
    /// Called by the Scheduler's <c>WorkItemCountsService</c> after each poll cycle.
    /// Pass <see langword="null"/> when there are no Pending items — the gauge emits no measurement.
    /// </summary>
    public static void UpdateOldestPendingAge(DateTimeOffset? oldestCreatedAt)
    {
        // Volatile.Write provides a release fence, matching the pattern used for _pollEpochMillis.
        Volatile.Write(ref _oldestPendingMillis,
            oldestCreatedAt.HasValue ? oldestCreatedAt.Value.ToUnixTimeMilliseconds() : 0L);
    }

    /// <summary>
    /// Pre-initializes all valid <c>result × reason</c> tag combinations for
    /// <see cref="DispatchAttempts"/> to zero, ensuring Prometheus <c>increase()</c> can see the
    /// first real increment after a deploy.
    ///
    /// Must be called from the API process only (not from Scheduler, Job Controller, or Agent).
    /// The convention is to call this from <c>Program.EmitPreInitCounters</c> in the API host.
    /// </summary>
    public static void PreInitializeDispatchAttempts()
    {
        // 7 valid combinations: dispatched/none + 3 deferred + 3 transient
        DispatchAttempts.Add(0,
            new KeyValuePair<string, object?>("result", "dispatched"),
            new KeyValuePair<string, object?>("reason", "none"));

        foreach (var reason in new[] { "concurrency_limit", "not_pending", "no_template" })
        {
            DispatchAttempts.Add(0,
                new KeyValuePair<string, object?>("result", "deferred"),
                new KeyValuePair<string, object?>("reason", reason));
        }

        foreach (var reason in new[] { "pvc_unavailable", "lock_timeout", "k8s_error" })
        {
            DispatchAttempts.Add(0,
                new KeyValuePair<string, object?>("result", "transient"),
                new KeyValuePair<string, object?>("reason", reason));
        }
    }

    /// <summary>
    /// Emits a structured Information-level log for terminal work item transitions and records
    /// <see cref="WorkItemsTerminated"/> and <see cref="JobExecutionDuration"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Recording point:</strong> run-level metrics (<c>pipeline.run.outcomes</c>,
    /// <c>pipeline.run.duration</c>) are recorded once per terminal transition in
    /// <c>WorkItemStatusTransitionService.EmitTerminalStatusTelemetryAsync</c> in the long-lived
    /// API process — not here and not in agent pods. This avoids the first-series-zero problem
    /// that affects ephemeral pods and the double-counting that occurred when the Job Controller
    /// also recorded metrics after POSTing status to the API. See issue #2967.
    /// </para>
    /// <para>
    /// <strong>Tag conventions:</strong> <c>failure_reason</c> values are snake_case
    /// (e.g. <c>"agent_error"</c>, <c>"timeout"</c>). This changed from PascalCase in issue #2967.
    /// Grafana queries filtering on <c>failure_reason</c> must be updated accordingly.
    /// </para>
    /// </remarks>
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

        // Normalize failure_reason to snake_case so that pre-initialized series labels match.
        // Breaking change from issue #2967: was PascalCase (e.g. "Timeout"), now snake_case ("timeout").
        var failureReasonTag = failureReason.HasValue
            ? PascalToSnakeCase(failureReason.Value.ToString())
            : "none";

        WorkItemsTerminated.Add(1,
            new KeyValuePair<string, object?>("status", status.ToString()),
            new KeyValuePair<string, object?>("failure_reason", failureReasonTag));

        // Record job execution duration (Dispatched → terminal)
        if (duration.HasValue && duration.Value.TotalSeconds >= 0)
        {
            JobExecutionDuration.Record(duration.Value.TotalSeconds,
                new KeyValuePair<string, object?>("status", status.ToString()));
        }
    }

    // Converts a PascalCase string to snake_case lowercase.
    // E.g. "AgentError" → "agent_error", "Timeout" → "timeout", "QualityGateExhausted" → "quality_gate_exhausted".
    // Mirrors ToFailureReasonTag() in PipelineRunInstrumentation (private partial class method) so that
    // the failure_reason tag on pipeline.jobs.failed carries consistent values from both emitters.
    // Uses a pre-compiled Regex to avoid repeated compilation; matchTimeout satisfies Sonar S6444.
    private static readonly Regex PascalCaseBoundaryRegex =
        new("(?<=[a-z0-9])([A-Z])", RegexOptions.None, matchTimeout: TimeSpan.FromSeconds(1));

    private static string PascalToSnakeCase(string pascalCase) =>
        PascalCaseBoundaryRegex.Replace(pascalCase, "_$1").ToLowerInvariant();
}
