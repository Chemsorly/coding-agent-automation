using System.Diagnostics;
using OpenTelemetry;
using OpenTelemetry.Context.Propagation;

namespace CodingAgent.Pipeline.Telemetry;

public static partial class PipelineTelemetry
{
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
    /// When an ambient <see cref="Activity.Current"/> already exists, its context is used directly
    /// rather than spawning an intermediate span — this prevents dead-branch orphan spans in the
    /// trace backend. A new span is only minted when there is no ambient activity context.
    /// </summary>
    public static Dictionary<string, string>? CaptureTraceContext(string activityName)
    {
        ArgumentException.ThrowIfNullOrEmpty(activityName);
        // Prefer the ambient span's context directly to avoid creating a short-lived dead-branch
        // Producer span that appears as an orphan in Grafana Tempo.
        var ctx = Activity.Current?.Context ?? default;
        if (ctx == default)
        {
            using var span = ActivitySource.StartActivity(activityName, ActivityKind.Producer);
            if (span is null)
                return null;
            ctx = span.Context;
        }

        var carrier = new Dictionary<string, string>();
        TraceContextPropagator.Inject(
            new PropagationContext(ctx, Baggage.Current),
            carrier,
            static (c, key, value) => c[key] = value);
        return carrier.Count > 0 ? carrier : null;
    }

    /// <summary>
    /// Serializes a W3C traceparent (and tracestate when present) from an existing
    /// <see cref="Activity"/> using the OTel <see cref="TraceContextPropagator"/>.
    /// Returns the <c>traceparent</c> header value, or <see langword="null"/> when
    /// <paramref name="activity"/> is <see langword="null"/>.
    /// Prefer this over manual string formatting to ensure <c>tracestate</c> is preserved.
    /// </summary>
    public static string? FormatTraceParent(Activity? activity)
    {
        if (activity is null) return null;
        var carrier = new Dictionary<string, string>();
        TraceContextPropagator.Inject(
            new PropagationContext(activity.Context, Baggage.Current),
            carrier,
            static (c, key, value) => c[key] = value);
        return carrier.GetValueOrDefault("traceparent");
    }
}
