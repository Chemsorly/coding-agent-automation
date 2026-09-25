using System.Diagnostics;
using CodingAgent.Pipeline.Models;

namespace CodingAgent.Pipeline.Telemetry;

/// <summary>
/// Disposable helper that encapsulates the shared telemetry pattern for pipeline run execution.
/// On creation: starts an <see cref="Activity"/> and sets standard tags.
/// On dispose: stops the activity.
/// </summary>
/// <remarks>
/// Metric recording (pipeline.run.outcomes, pipeline.run.duration) is no longer performed here —
/// it is consolidated in <c>WorkItemStatusTransitionService.EmitTerminalStatusTelemetryAsync</c>
/// in the long-lived API process, which avoids the first-series-zero problem that affects
/// ephemeral agent pods. See issue #2967.
///
/// <see cref="MarkCompleted"/> and <see cref="MarkFailed"/> still set the span status;
/// <see cref="StopTiming"/> is a no-op retained for call-site compatibility.
///
/// Call <see cref="MarkCompleted"/> before disposal to mark the span as successful.
/// If not called, the run span is left without an explicit OK status.
/// Use via a <c>using</c> statement.
/// </remarks>
public sealed partial class PipelineRunInstrumentation : IDisposable
{
    /// <summary>The tracing <see cref="Activity"/> for this run, or <see langword="null"/> if no listener is registered.</summary>
    /// <remarks>
    /// Activity is set once during construction and disposed in <see cref="Dispose"/>. Callers should
    /// not access this property after disposal; the underlying <see cref="Activity"/> will already be stopped.
    /// </remarks>
    public Activity? Activity { get; }

    private bool _disposed;

    private PipelineRunInstrumentation(Activity? activity)
    {
        Activity = activity;
    }

    /// <summary>
    /// Creates a new <see cref="PipelineRunInstrumentation"/> for a pipeline run.
    /// Starts an activity and sets standard tags.
    /// </summary>
    /// <param name="runId">The pipeline run identifier (set as <c>pipeline.run_id</c> tag).</param>
    /// <param name="issueIdentifier">The issue identifier (set as <c>pipeline.issue</c> tag).</param>
    /// <param name="runType">The type of pipeline run (used for activity tags).</param>
    /// <param name="projectId">The project identifier (set as <c>pipeline.project_id</c> tag).</param>
    /// <param name="projectName">The project name (set as <c>pipeline.project_name</c> tag).</param>
    /// <param name="kind">The <see cref="ActivityKind"/> for the activity. Defaults to <see cref="ActivityKind.Internal"/>.</param>
    /// <param name="parentContext">Optional parent <see cref="ActivityContext"/> for trace propagation.</param>
    /// <param name="meterFactory">Ignored. Retained for call-site compatibility; no instruments are created.</param>
    // TODO: [WARNING] The meterFactory parameter was narrowed from IMeterFactory? to object? when metric
    // recording was removed from this class (issue #2967). Callers that previously passed an IMeterFactory
    // for test isolation now pass it as object? without a compile error, but the factory is silently ignored.
    // If any call site passes a non-null IMeterFactory believing it provides meter isolation, it is silently
    // a no-op. Consider removing the parameter entirely on the next breaking-change opportunity, or adding
    // an [Obsolete] attribute to signal that it is ignored.
    public static PipelineRunInstrumentation Start(
        string runId, string issueIdentifier,
        PipelineRunType runType, string? projectId, string? projectName,
        ActivityKind kind = ActivityKind.Internal,
        ActivityContext parentContext = default,
        object? meterFactory = null)
    {
        var activity = PipelineTelemetry.ActivitySource.StartActivity("ExecutePipeline", kind, parentContext);
        activity?.SetTag("pipeline.run_id", runId);
        activity?.SetTag("pipeline.issue", issueIdentifier);
        activity?.SetTag("pipeline.run_type", runType.ToString());
        PipelineTelemetry.SetProjectTags(activity, projectId, projectName);

        return new PipelineRunInstrumentation(activity);
    }

    /// <summary>
    /// Marks the run as successfully completed.
    /// Sets the span status to OK.
    /// </summary>
    public void MarkCompleted()
    {
        Activity?.SetStatus(ActivityStatusCode.Ok);
    }

    /// <summary>
    /// Records the failure reason for this run on the span.
    /// </summary>
    public void MarkFailed(FailureReason? reason = null)
    {
        if (reason.HasValue)
            Activity?.SetTag("pipeline.failure_reason", reason.Value.ToString());
    }

    /// <summary>
    /// No-op. Retained for call-site compatibility.
    /// (Timing was previously used to gate metric recording; metrics are now emitted by the API.)
    /// </summary>
    public void StopTiming() { }

    /// <summary>
    /// Stops the activity.
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        Activity?.Dispose();
        GC.SuppressFinalize(this);
    }
}
