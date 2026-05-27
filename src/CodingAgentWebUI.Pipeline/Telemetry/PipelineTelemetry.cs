using System.Diagnostics;
using System.Diagnostics.Metrics;
using CodingAgentWebUI.Pipeline.Models;

namespace CodingAgentWebUI.Pipeline.Telemetry;

/// <summary>
/// Central telemetry definitions for the pipeline. Provides an <see cref="ActivitySource"/>
/// for distributed tracing and a <see cref="Meter"/> for metrics.
/// </summary>
public static class PipelineTelemetry
{
    public const string SourceName = "CodingAgent.Pipeline";

    public static readonly ActivitySource ActivitySource = new(SourceName);
    public static readonly Meter Meter = new(SourceName);

    public static readonly Counter<long> JobsDispatched = Meter.CreateCounter<long>("pipeline.jobs.dispatched");
    public static readonly Counter<long> JobsCompleted = Meter.CreateCounter<long>("pipeline.jobs.completed");
    public static readonly Counter<long> JobsFailed = Meter.CreateCounter<long>("pipeline.jobs.failed");
    public static readonly Histogram<double> JobDuration = Meter.CreateHistogram<double>(
        "pipeline.jobs.duration", "s", "Duration of pipeline jobs in seconds");

    /// <summary>Creates a run_type tag from the given <see cref="PipelineRunType"/>.</summary>
    public static KeyValuePair<string, object?> RunTypeTag(PipelineRunType runType) =>
        new("run_type", runType.ToString().ToLowerInvariant());
}
