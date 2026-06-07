using System.Diagnostics;
using System.Diagnostics.Metrics;
using CodingAgentWebUI.Pipeline.Models;
using OpenTelemetry;
using OpenTelemetry.Context.Propagation;

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

    public static readonly Counter<long> SubIssuesCreated = Meter.CreateCounter<long>("pipeline.decomposition.sub_issues.created");
    public static readonly Counter<long> SubIssuesFailed = Meter.CreateCounter<long>("pipeline.decomposition.sub_issues.failed");
    public static readonly Histogram<double> DecompositionDuration = Meter.CreateHistogram<double>(
        "pipeline.decomposition.duration", "s", "Duration of decomposition phases in seconds");

    public static readonly Histogram<double> StepDuration = Meter.CreateHistogram<double>(
        "pipeline.step.duration", "s", "Duration of individual pipeline steps");
    public static readonly Counter<long> StepCount = Meter.CreateCounter<long>(
        "pipeline.step.count", "{step}", "Pipeline step execution count");
    public static readonly Counter<long> TokensUsed = Meter.CreateCounter<long>(
        "agent.tokens.used", "{token}", "Agent tokens consumed");

    public static readonly Counter<long> QualityGateRetries = Meter.CreateCounter<long>(
        "quality_gate.retries", "{retry}", "Quality gate retry attempts");
    public static readonly Histogram<double> QualityGateDuration = Meter.CreateHistogram<double>(
        "quality_gate.duration", "s", "Total time in quality gate phase");
    public static readonly Counter<long> QualityGateEvaluations = Meter.CreateCounter<long>(
        "quality_gate.evaluations", "{evaluation}", "Individual gate evaluation events");
    public static readonly Histogram<double> ExternalCiDuration = Meter.CreateHistogram<double>(
        "quality_gate.external_ci.duration", "s", "Time waiting for external CI");

    public static readonly Histogram<double> QueueWaitTime = Meter.CreateHistogram<double>(
        "dispatch.queue.wait_time", "s", "Time a job spent waiting in the dispatch queue");

    public static readonly Counter<long> AgentJobsReceived = Meter.CreateCounter<long>(
        "agent.jobs.received", "{job}", "Jobs received by agent workers");
    public static readonly Counter<long> AgentJobsRejected = Meter.CreateCounter<long>(
        "agent.jobs.rejected", "{job}", "Jobs rejected by agent workers");
    public static readonly Counter<long> AgentHeartbeatFailures = Meter.CreateCounter<long>(
        "agent.heartbeat.failures", "{failure}", "Agent heartbeat failures");
    public static readonly Counter<long> AgentReconnections = Meter.CreateCounter<long>(
        "agent.reconnections", "{reconnection}", "Agent reconnection events");

    internal static class AgentRejectionReasons
    {
        public const string Busy = "busy";
        public const string ShuttingDown = "shutting_down";
        public const string Unknown = "unknown";
    }

    internal static class QualityGateNames
    {
        public const string Compilation = "compilation";
        public const string Tests = "tests";
        public const string Coverage = "coverage";
        public const string Security = "security";
        public const string ExternalCi = "external_ci";
    }

    /// <summary>
    /// Builds a <see cref="TagList"/> for per-step metrics, including step_name and project context.
    /// </summary>
    public static TagList BuildStepTags(string stepName, PipelineRun run) =>
        new(
        [
            new KeyValuePair<string, object?>("step_name", stepName),
            RunTypeTag(run.RunType),
            ProjectIdTag(run.ProjectId),
            ProjectNameTag(run.ProjectName)
        ]);

    /// <summary>Creates a run_type tag from the given <see cref="PipelineRunType"/>.</summary>
    public static KeyValuePair<string, object?> RunTypeTag(PipelineRunType runType) =>
        new("run_type", runType.ToString().ToLowerInvariant());

    /// <summary>Creates a pipeline.project_id tag.</summary>
    public static KeyValuePair<string, object?> ProjectIdTag(string? projectId) =>
        new("pipeline.project_id", projectId ?? "unknown");

    /// <summary>Creates a pipeline.project_name tag.</summary>
    public static KeyValuePair<string, object?> ProjectNameTag(string? projectName) =>
        new("pipeline.project_name", projectName ?? "unknown");

    /// <summary>
    /// Sets project-related tags on an <see cref="Activity"/>.
    /// </summary>
    public static void SetProjectTags(Activity? activity, string? projectId, string? projectName)
    {
        activity?.SetTag("pipeline.project_id", projectId ?? "unknown");
        activity?.SetTag("pipeline.project_name", projectName ?? "unknown");
    }

    /// <summary>
    /// Builds a <see cref="TagList"/> containing run_type, project_id, and project_name tags.
    /// Use this when recording metrics that should include project context.
    /// </summary>
    public static TagList BuildTags(PipelineRunType runType, string? projectId, string? projectName) =>
        new(
        [
            RunTypeTag(runType),
            ProjectIdTag(projectId),
            ProjectNameTag(projectName)
        ]);

    private static readonly TraceContextPropagator TraceContextPropagator = new();

    /// <summary>
    /// Extracts a parent <see cref="ActivityContext"/> from a W3C trace context dictionary.
    /// Returns <see langword="default"/> when the dictionary is null or empty,
    /// causing <see cref="ActivitySource.StartActivity"/> to create a root span.
    /// </summary>
    public static ActivityContext ExtractTraceContext(Dictionary<string, string>? traceContext)
    {
        if (traceContext is not { Count: > 0 })
            return default;

        var parentContext = TraceContextPropagator.Extract(
            default,
            traceContext,
            static (c, key) => c.TryGetValue(key, out var val) ? [val] : []);
        return parentContext.ActivityContext;
    }

    /// <summary>
    /// Creates a short-lived <see cref="ActivityKind.Producer"/> span and captures its
    /// W3C trace context (traceparent + tracestate) into a dictionary suitable for serialization.
    /// This guarantees a traceparent is always produced regardless of ambient Activity.Current.
    /// </summary>
    public static Dictionary<string, string>? CaptureTraceContext(string activityName)
    {
        using var activity = ActivitySource.StartActivity(activityName, ActivityKind.Producer);
        if (activity is null)
            return null;

        var carrier = new Dictionary<string, string>();
        TraceContextPropagator.Inject(
            new PropagationContext(activity.Context, Baggage.Current),
            carrier,
            static (c, key, value) => c[key] = value);
        return carrier.Count > 0 ? carrier : null;
    }
}
