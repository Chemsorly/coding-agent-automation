using System.Diagnostics.Metrics;
using System.Text.RegularExpressions;
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
        // Volatile.Write provides a release fence, ensuring that any preceding writes
        // are visible to other threads before they observe the new epoch value.
        // Collapses the old two-field (_lastPollEpochSeconds / _pollEpochRecorded) pattern
        // into one field to prevent the export thread from observing an inconsistent pair.
        Volatile.Write(ref _pollEpochMillis, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
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
    /// Also emits <see cref="PipelineTelemetry.JobsCompleted"/>, <see cref="PipelineTelemetry.JobsFailed"/>,
    /// and <see cref="PipelineTelemetry.JobDuration"/> from the long-lived Job Controller process to avoid
    /// the pod-exit OTLP flush race that affects the agent-side recordings of the same instruments.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Double-counting:</strong> <see cref="PipelineRunInstrumentation.Dispose"/> also records
    /// these same <c>pipeline.jobs.*</c> instruments from the ephemeral agent pod with richer tags
    /// (<c>run_type</c>, <c>pipeline.project_id</c>, <c>pipeline.project_name</c>). When both the
    /// agent-pod flush and this Job Controller recording succeed for the same job,
    /// <c>pipeline_jobs_completed_total</c> / <c>pipeline_jobs_failed_total</c> will increment by 2 in
    /// Prometheus. This is intentional: the metrics are now "at-least-once" reliable. The agent-pod
    /// recording provides higher-fidelity tags and still works when the pod exits cleanly.
    /// Use <c>workdistribution_workitems_terminated_total</c> for exact job counts.
    /// </para>
    /// <para>
    /// <strong>Tag conventions:</strong> the Job Controller-side series carry <c>status</c> and (for
    /// failures) <c>failure_reason</c> tags only — no <c>run_type</c> or project tags.
    /// <c>failure_reason</c> values are snake_case (e.g. <c>"agent_error"</c>, <c>"timeout"</c>) to
    /// match the agent-pod series on the same metric family. The emitter is further distinguishable
    /// by <c>service.name=coding-agent-jobcontroller</c> vs the agent pod's <c>service.name</c>.
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

        WorkItemsTerminated.Add(1,
            new KeyValuePair<string, object?>("status", status.ToString()),
            new KeyValuePair<string, object?>("failure_reason", failureReason?.ToString() ?? "none"));

        // Record job execution duration (Dispatched → terminal)
        if (duration.HasValue && duration.Value.TotalSeconds >= 0)
        {
            JobExecutionDuration.Record(duration.Value.TotalSeconds,
                new KeyValuePair<string, object?>("status", status.ToString()));
        }

        // Emit pipeline.jobs.* from the long-lived Job Controller to make these metrics reliable.
        // See XML doc above for double-counting and tag-convention notes.
        var statusTag = new KeyValuePair<string, object?>("status", status.ToString());
        if (status == WorkItemStatus.Succeeded)
        {
            // NOTE: statusTag adds a "status" label to pipeline_jobs_completed_total that the
            // agent-pod emitter (PipelineRunInstrumentation.Dispose) does NOT include. This creates two
            // structurally incompatible label sets on the same metric family: the Job Controller series
            // has {status="Succeeded"} while the agent-pod series has no status label. A bare
            // increase(pipeline_jobs_completed_total[24h]) sums both correctly, but any Prometheus query
            // filtering on {status="Succeeded"} will silently exclude agent-pod recordings.
            // See observability.md — "Reliable sources by use case" for query guidance.
            PipelineTelemetry.JobsCompleted.Add(1, statusTag);
        }
        else
        {
            // NOTE: WorkItemStatus.Cancelled is a real terminal status that reaches this else branch
            // (via WorkItemEndpoints.EmitTerminalStatusTelemetryAsync), causing it to increment
            // pipeline_jobs_failed_total with status="Cancelled", failure_reason="unknown". This
            // inflates the failure counter and diverges from the agent-side PipelineRunInstrumentation
            // which emits nothing for cancellations. Use workdistribution_workitems_terminated_total
            // (which tags status accurately) for exact counts by status.
            // snake_case failure_reason matches PipelineRunInstrumentation.Dispose() convention so
            // that label-filtered Prometheus queries work uniformly across both emitters.
            var failureReasonSnake = failureReason.HasValue
                ? PascalToSnakeCase(failureReason.Value.ToString())
                : "unknown";
            PipelineTelemetry.JobsFailed.Add(1,
                statusTag,
                new KeyValuePair<string, object?>("failure_reason", failureReasonSnake));
        }

        if (duration.HasValue && duration.Value.TotalSeconds >= 0)
            PipelineTelemetry.JobDuration.Record(duration.Value.TotalSeconds, statusTag);
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
