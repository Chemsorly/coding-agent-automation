using System.Diagnostics;
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
public sealed class PipelineRunInstrumentation : IDisposable
{
    // TODO: Activity is publicly accessible with no guard against post-disposal access. Callers set tags
    // on it before disposal, but if accessed after Dispose() the activity will already be stopped/disposed.
    // Consider documenting this contract or making Activity inaccessible after disposal.
    /// <summary>The tracing <see cref="Activity"/> for this run, or <see langword="null"/> if no listener is registered.</summary>
    public Activity? Activity { get; }

    private readonly Stopwatch _stopwatch;
    private readonly TagList _tags;
    private readonly PipelineRunType _runType;
    private readonly string? _projectId;
    private readonly string? _projectName;
    private bool _completed;
    private bool _disposed;
    private FailureReason? _failureReason;

    private PipelineRunInstrumentation(
        Activity? activity, TagList tags,
        PipelineRunType runType, string? projectId, string? projectName)
    {
        Activity = activity;
        _tags = tags;
        _runType = runType;
        _projectId = projectId;
        _projectName = projectName;
        _stopwatch = Stopwatch.StartNew();
        PipelineTelemetry.JobsDispatched.Add(1, tags);
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
    public static PipelineRunInstrumentation Start(
        string runId, string issueIdentifier,
        PipelineRunType runType, string? projectId, string? projectName,
        ActivityKind kind = ActivityKind.Internal,
        ActivityContext parentContext = default)
    {
        var activity = PipelineTelemetry.ActivitySource.StartActivity("ExecutePipeline", kind, parentContext);
        activity?.SetTag("pipeline.run_id", runId);
        activity?.SetTag("pipeline.issue", issueIdentifier);
        PipelineTelemetry.SetProjectTags(activity, projectId, projectName);
        var tags = PipelineTelemetry.BuildTags(runType, projectId, projectName);
        return new PipelineRunInstrumentation(activity, tags, runType, projectId, projectName);
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
        PipelineTelemetry.JobDuration.Record(_stopwatch.Elapsed.TotalSeconds, _tags);

        if (_runType is PipelineRunType.DecompositionAnalysis or PipelineRunType.Decomposition)
        {
            // PipelineRunInstrumentation.Start() is only called from LocalPipelineExecutor.ExecuteAsync()
            // (the agent-side execution path). PipelineOrchestrationService does not call it.
            // DecompositionDuration is therefore always emitted from the agent side — the correct
            // emission path. No caller-provided flag is needed.
            var phase = _runType == PipelineRunType.DecompositionAnalysis ? "analysis" : "creation";
            PipelineTelemetry.DecompositionDuration.Record(_stopwatch.Elapsed.TotalSeconds,
                PipelineTelemetry.ProjectIdTag(_projectId),
                PipelineTelemetry.ProjectNameTag(_projectName),
                new KeyValuePair<string, object?>("phase", phase));
        }

        if (_completed)
            PipelineTelemetry.JobsCompleted.Add(1, _tags);
        else
        {
            var tagValue = _failureReason.HasValue
                ? ToFailureReasonTag(_failureReason.Value)
                : "unknown";
            // TODO: TagList is a value-type struct with an inline capacity of 8 key-value pairs. The
            // struct copy below (`var failureTags = _tags`) is intentional — it keeps `_tags` clean for
            // the JobDuration/JobsCompleted paths. However, when the stored tag count reaches 8, TagList
            // overflows to a heap-allocated list that is NOT deep-copied by value assignment; both
            // instances share the same underlying array, so the `Add("failure_reason", ...)` call would
            // mutate `_tags` and corrupt subsequent metrics. Current tag count is 3 (run_type,
            // project_id, project_name), so this is safe today, but adding 6 or more standard tags
            // would silently trigger the hazard. Fix before adding more tags: allocate a fresh TagList
            // from `_tags` with explicit capacity (TagList does not expose a copy-constructor as of
            // .NET 8, so enumerate and re-add, or switch to a TagList helper that always owns its storage).
            var failureTags = _tags;
            failureTags.Add(new KeyValuePair<string, object?>("failure_reason", tagValue));
            PipelineTelemetry.JobsFailed.Add(1, failureTags);
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
        System.Text.RegularExpressions.Regex.Replace(reason.ToString(), "(?<=[a-z0-9])([A-Z])", "_$1")
              .ToLowerInvariant();
}
