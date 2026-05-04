using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace CodingAgentWebUI.Pipeline.Telemetry;

/// <summary>
/// Centralized OpenTelemetry instrumentation for the pipeline.
/// Provides an <see cref="ActivitySource"/> for distributed tracing and a <see cref="Meter"/> for metrics.
/// </summary>
public static class PipelineTelemetry
{
    public const string SourceName = "CodingAgent.Pipeline";

    public static readonly ActivitySource ActivitySource = new(SourceName);
    public static readonly Meter Meter = new(SourceName);

    public static readonly Counter<long> JobsDispatched = Meter.CreateCounter<long>(
        "pipeline.jobs.dispatched", description: "Number of pipeline jobs dispatched to agents");
    public static readonly Counter<long> JobsCompleted = Meter.CreateCounter<long>(
        "pipeline.jobs.completed", description: "Number of pipeline jobs completed successfully");
    public static readonly Counter<long> JobsFailed = Meter.CreateCounter<long>(
        "pipeline.jobs.failed", description: "Number of pipeline jobs that failed");
    public static readonly Histogram<double> JobDuration = Meter.CreateHistogram<double>(
        "pipeline.jobs.duration", "s", "Duration of pipeline jobs in seconds");
}
