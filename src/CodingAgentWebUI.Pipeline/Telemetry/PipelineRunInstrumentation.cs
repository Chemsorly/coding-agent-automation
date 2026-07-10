using System.Diagnostics;
using CodingAgentWebUI.Pipeline.Models;

namespace CodingAgentWebUI.Pipeline.Telemetry;

/// <summary>
/// Disposable helper that encapsulates the standard pipeline run telemetry pattern:
/// start activity → set tags → start stopwatch → increment dispatched counter →
/// on dispose: record duration, increment completed/failed counter, set final tags.
/// </summary>
/// <remarks>
/// <para>
/// Callers use <see cref="Start"/> to begin instrumentation and <see cref="MarkCompleted"/>,
/// <see cref="MarkCancelled"/>, and <see cref="SetFinalStep"/> to indicate outcome before
/// the helper is disposed. Error recording (SetStatus, RecordError) remains in the caller's
/// catch blocks — this helper only handles bookkeeping telemetry (counters, duration, tags).
/// </para>
/// <para>
/// The <see cref="Activity"/> property is exposed so callers can set status and record
/// exceptions in their catch blocks before dispose runs.
/// </para>
/// </remarks>
public sealed class PipelineRunInstrumentation : IDisposable
{
    private readonly Activity? _activity;
    private readonly Stopwatch _stopwatch;
    private readonly TagList _tags;
    private readonly PipelineRunType _runType;
    private readonly string? _projectId;
    private readonly string? _projectName;
    private bool _completed;
    private bool _cancelled;
    private string? _finalStep;

    private PipelineRunInstrumentation(Activity? activity, TagList tags,
        PipelineRunType runType, string? projectId, string? projectName)
    {
        _activity = activity;
        _tags = tags;
        _runType = runType;
        _projectId = projectId;
        _projectName = projectName;
        _stopwatch = Stopwatch.StartNew();
        PipelineTelemetry.JobsDispatched.Add(1, _tags);
    }

    /// <summary>Gets the underlying <see cref="System.Diagnostics.Activity"/> for error recording in caller catch blocks.</summary>
    public Activity? Activity => _activity;

    /// <summary>
    /// Starts pipeline run instrumentation: creates an activity, sets standard tags,
    /// starts a stopwatch, and increments the dispatched counter.
    /// </summary>
    public static PipelineRunInstrumentation Start(
        string runId, string? issueIdentifier,
        PipelineRunType runType, string? projectId, string? projectName,
        string? agentId = null,
        ActivityKind kind = ActivityKind.Internal,
        ActivityContext parentContext = default)
    {
        var activity = PipelineTelemetry.ActivitySource.StartActivity(
            "ExecutePipeline", kind, parentContext);
        activity?.SetTag("pipeline.run_id", runId);
        activity?.SetTag("pipeline.issue", issueIdentifier);
        if (agentId is not null)
            activity?.SetTag("pipeline.agent_id", agentId);
        PipelineTelemetry.SetProjectTags(activity, projectId, projectName);

        var tags = PipelineTelemetry.BuildTags(runType, projectId, projectName);
        return new PipelineRunInstrumentation(activity, tags, runType, projectId, projectName);
    }

    /// <summary>Marks the run as successfully completed. Must be called before dispose.</summary>
    public void MarkCompleted() => _completed = true;

    /// <summary>Marks the run as cancelled. Must be called before dispose.</summary>
    public void MarkCancelled() => _cancelled = true;

    /// <summary>Sets the final pipeline step name for activity tagging. Must be called before dispose.</summary>
    public void SetFinalStep(string step) => _finalStep = step;

    /// <summary>
    /// Records duration, increments success/failure counters, sets final activity tags,
    /// and disposes the activity.
    /// </summary>
    // TODO: Add a _disposed guard flag to prevent double-counting metrics if Dispose() is called twice.
    public void Dispose()
    {
        _stopwatch.Stop();
        // TODO: Duration now includes provider disposal time in LocalPipelineExecutor because `using var`
        // defers Dispose until after the try/catch/finally completes. Consider stopping the stopwatch
        // explicitly before provider disposal if exact parity with old behavior is needed.
        PipelineTelemetry.JobDuration.Record(_stopwatch.Elapsed.TotalSeconds, _tags);

        // TODO: DecompositionDuration is now emitted from the orchestrator path as well (previously
        // only emitted from LocalPipelineExecutor). Verify this is acceptable or gate on caller context.
        if (_runType is PipelineRunType.DecompositionAnalysis or PipelineRunType.Decomposition)
        {
            var phase = _runType == PipelineRunType.DecompositionAnalysis ? "analysis" : "creation";
            PipelineTelemetry.DecompositionDuration.Record(_stopwatch.Elapsed.TotalSeconds,
                PipelineTelemetry.ProjectIdTag(_projectId),
                PipelineTelemetry.ProjectNameTag(_projectName),
                new KeyValuePair<string, object?>("phase", phase));
        }

        if (_completed)
            PipelineTelemetry.JobsCompleted.Add(1, _tags);
        else
            PipelineTelemetry.JobsFailed.Add(1, _tags);

        if (_finalStep is not null)
            _activity?.SetTag("pipeline.final_step", _finalStep);
        if (_cancelled)
            _activity?.SetTag("pipeline.cancelled", true);

        _activity?.Dispose();
    }
}
