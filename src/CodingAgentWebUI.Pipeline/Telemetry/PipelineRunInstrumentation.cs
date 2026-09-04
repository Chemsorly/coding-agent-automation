using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Text.RegularExpressions;
using CodingAgentWebUI.Pipeline.Models;

namespace CodingAgentWebUI.Pipeline.Telemetry;

/// <summary>
/// Disposable helper that encapsulates the shared telemetry pattern for pipeline run execution.
/// On creation: starts an <see cref="Activity"/>, sets standard tags, starts a <see cref="Stopwatch"/>,
/// and records the <see cref="PipelineTelemetry.JobsDispatched"/> counter.
/// On dispose: records <see cref="PipelineTelemetry.JobDuration"/>, decomposition duration (if applicable),
/// and <see cref="PipelineTelemetry.JobsCompleted"/> or <see cref="PipelineTelemetry.JobsFailed"/> counters.
/// </summary>
/// <remarks>
/// Call <see cref="MarkCompleted"/> before disposal to indicate a successful run.
/// If not called, the run is recorded as failed. Use via a <c>using</c> statement to ensure
/// metrics are always recorded regardless of exception flow.
/// </remarks>
public sealed partial class PipelineRunInstrumentation : IDisposable
{
    /// <summary>The tracing <see cref="Activity"/> for this run, or <see langword="null"/> if no listener is registered.</summary>
    /// <remarks>
    /// Activity is set once during construction and disposed in <see cref="Dispose"/>. Callers should
    /// not access this property after disposal; the underlying <see cref="Activity"/> will already be stopped.
    /// </remarks>
    public Activity? Activity { get; }

    private readonly Stopwatch _stopwatch;
    private readonly TagList _tags;
    private readonly PipelineRunType _runType;
    private readonly string? _projectId;
    private readonly string? _projectName;
    private bool _completed;
    private bool _disposed;
    private FailureReason? _failureReason;

    // Instrument references — either the static PipelineTelemetry fields or factory-created instances
    private readonly Counter<long> _jobsDispatched;
    private readonly Counter<long> _jobsCompleted;
    private readonly Counter<long> _jobsFailed;
    private readonly Histogram<double> _jobDuration;
    private readonly Histogram<double> _decompositionDuration;

    private PipelineRunInstrumentation(
        Activity? activity, TagList tags,
        PipelineRunType runType, string? projectId, string? projectName,
        Counter<long> jobsDispatched, Counter<long> jobsCompleted, Counter<long> jobsFailed,
        Histogram<double> jobDuration, Histogram<double> decompositionDuration)
    {
        Activity = activity;
        _tags = tags;
        _runType = runType;
        _projectId = projectId;
        _projectName = projectName;
        _stopwatch = Stopwatch.StartNew();
        _jobsDispatched = jobsDispatched;
        _jobsCompleted = jobsCompleted;
        _jobsFailed = jobsFailed;
        _jobDuration = jobDuration;
        _decompositionDuration = decompositionDuration;
        _jobsDispatched.Add(1, tags);
    }

    /// <summary>
    /// Creates a new <see cref="PipelineRunInstrumentation"/> for a pipeline run.
    /// Starts an activity, sets standard tags, and begins timing.
    /// </summary>
    /// <param name="runId">The pipeline run identifier (set as <c>pipeline.run_id</c> tag).</param>
    /// <param name="issueIdentifier">The issue identifier (set as <c>pipeline.issue</c> tag).</param>
    /// <param name="runType">The type of pipeline run (used for metric tags and decomposition duration).</param>
    /// <param name="projectId">The project identifier (set as <c>pipeline.project_id</c> tag).</param>
    /// <param name="projectName">The project name (set as <c>pipeline.project_name</c> tag).</param>
    /// <param name="kind">The <see cref="ActivityKind"/> for the activity. Defaults to <see cref="ActivityKind.Internal"/>.</param>
    /// <param name="parentContext">Optional parent <see cref="ActivityContext"/> for trace propagation.</param>
    /// <param name="meterFactory">Optional meter factory. When provided, instruments are created from an isolated meter instead of the static <see cref="PipelineTelemetry"/> fields.</param>
    public static PipelineRunInstrumentation Start(
        string runId, string issueIdentifier,
        PipelineRunType runType, string? projectId, string? projectName,
        ActivityKind kind = ActivityKind.Internal,
        ActivityContext parentContext = default,
        IMeterFactory? meterFactory = null)
    {
        var activity = PipelineTelemetry.ActivitySource.StartActivity("ExecutePipeline", kind, parentContext);
        activity?.SetTag("pipeline.run_id", runId);
        activity?.SetTag("pipeline.issue", issueIdentifier);
        activity?.SetTag("pipeline.run_type", runType.ToString());
        PipelineTelemetry.SetProjectTags(activity, projectId, projectName);
        var tags = PipelineTelemetry.BuildTags(runType, projectId, projectName);

        Counter<long> jobsDispatched, jobsCompleted, jobsFailed;
        Histogram<double> jobDuration, decompositionDuration;

        if (meterFactory is not null)
        {
            var meter = meterFactory.Create(new MeterOptions(PipelineTelemetry.SourceName));
            jobsDispatched = meter.CreateCounter<long>("pipeline.jobs.dispatched");
            jobsCompleted = meter.CreateCounter<long>("pipeline.jobs.completed");
            jobsFailed = meter.CreateCounter<long>("pipeline.jobs.failed");
            jobDuration = meter.CreateHistogram<double>("pipeline.jobs.duration", "s", "Duration of pipeline jobs in seconds",
                new InstrumentAdvice<double> { HistogramBucketBoundaries = [30, 60, 120, 300, 600, 900, 1200, 1800, 2700, 3600, 5400, 7200, 10800, 14400, 18000, 21600] });
            decompositionDuration = meter.CreateHistogram<double>("pipeline.decomposition.duration", "s", "Duration of decomposition phases in seconds");
        }
        else
        {
            jobsDispatched = PipelineTelemetry.JobsDispatched;
            jobsCompleted = PipelineTelemetry.JobsCompleted;
            jobsFailed = PipelineTelemetry.JobsFailed;
            jobDuration = PipelineTelemetry.JobDuration;
            decompositionDuration = PipelineTelemetry.DecompositionDuration;
        }

        return new PipelineRunInstrumentation(activity, tags, runType, projectId, projectName,
            jobsDispatched, jobsCompleted, jobsFailed, jobDuration, decompositionDuration);
    }

    /// <summary>
    /// Marks the run as successfully completed. Must be called before disposal
    /// to record the run in <see cref="PipelineTelemetry.JobsCompleted"/> rather than
    /// <see cref="PipelineTelemetry.JobsFailed"/>.
    /// </summary>
    public void MarkCompleted() => _completed = true;

    /// <summary>
    /// Records the failure reason for this run. Does not affect the completed/failed decision —
    /// if <see cref="MarkCompleted"/> is not called, the run is always recorded as failed.
    /// The <paramref name="reason"/> is emitted as the <c>failure_reason</c> tag on
    /// <see cref="PipelineTelemetry.JobsFailed"/>. Pass <see langword="null"/> to emit
    /// <c>"unknown"</c> (same as not calling this method at all).
    /// </summary>
    public void MarkFailed(FailureReason? reason = null) => _failureReason = reason;

    /// <summary>
    /// Stops the internal stopwatch without recording metrics or disposing the activity.
    /// Call before expensive cleanup to avoid inflating the duration metric.
    /// Idempotent — safe to call multiple times or before Dispose().
    /// </summary>
    public void StopTiming() => _stopwatch.Stop();

    /// <summary>
    /// Stops timing, records duration and success/failure counters.
    /// Also records decomposition-specific duration for decomposition run types.
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _stopwatch.Stop();
        _jobDuration.Record(_stopwatch.Elapsed.TotalSeconds, _tags);

        if (_runType is PipelineRunType.DecompositionAnalysis or PipelineRunType.Decomposition)
        {
            // PipelineRunInstrumentation.Start() is only called from LocalPipelineExecutor.ExecuteAsync()
            // (the agent-side execution path). PipelineOrchestrationService does not call it.
            // DecompositionDuration is therefore always emitted from the agent side — the correct
            // emission path. No caller-provided flag is needed.
            var phase = _runType == PipelineRunType.DecompositionAnalysis ? "analysis" : "creation";
            _decompositionDuration.Record(_stopwatch.Elapsed.TotalSeconds,
                PipelineTelemetry.ProjectIdTag(_projectId),
                PipelineTelemetry.ProjectNameTag(_projectName),
                new KeyValuePair<string, object?>("phase", phase));
        }

        if (_completed)
            _jobsCompleted.Add(1, _tags);
        else
        {
            var tagValue = _failureReason.HasValue
                ? ToFailureReasonTag(_failureReason.Value)
                : "unknown";
            // Note: TagList is a value-type struct with an inline capacity of 8 key-value pairs.
            // The copy below is intentional — it keeps _tags clean for the JobDuration/JobsCompleted paths.
            // TagList overflows to a heap-allocated list beyond 8 entries, and that overflow list is NOT
            // deep-copied by value assignment. Current tag count is 3 (run_type, project_id, project_name),
            // so this is safe. Do not add 6 or more standard tags without revisiting this copy strategy.
            var failureTags = _tags;
            failureTags.Add(new KeyValuePair<string, object?>("failure_reason", tagValue));
            _jobsFailed.Add(1, failureTags);
        }

        Activity?.Dispose();
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Converts a <see cref="FailureReason"/> enum member name from PascalCase to snake_case lowercase.
    /// For example: <c>QualityGateExhausted</c> → <c>"quality_gate_exhausted"</c>,
    /// <c>AgentError</c> → <c>"agent_error"</c>, <c>Timeout</c> → <c>"timeout"</c>.
    /// </summary>
    private static string ToFailureReasonTag(FailureReason reason) =>
        PascalCaseBoundaryRegex().Replace(reason.ToString(), "_$1").ToLowerInvariant();

    [GeneratedRegex("(?<=[a-z0-9])([A-Z])", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    private static partial Regex PascalCaseBoundaryRegex();
}
