using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace CodingAgent.Pipeline.Telemetry;

/// <summary>
/// Central telemetry definitions for the pipeline. Provides an <see cref="ActivitySource"/>
/// for distributed tracing and a <see cref="Meter"/> for metrics.
/// </summary>
public static partial class PipelineTelemetry
{
    public const string SourceName = "CodingAgent.Pipeline";

    // UCUM-style unit annotation constants shared across counter definitions.
    // Defined here to avoid S1192 (repeated string literals) and to make unit
    // semantics explicit at the call site.
    private const string UnitUpdate = "{update}";
    private const string UnitFailure = "{failure}";
    private const string UnitItem = "{item}";
    private const string UnitRun = "{run}";
    private const string UnitEvent = "{event}";
    private const string UnitReprobe = "{reprobe}";

    public static readonly ActivitySource ActivitySource = new(SourceName);
    public static readonly Meter Meter = new(SourceName);

    // ── Agent observable gauges (Scheduler-leader-emitted) ───────────────────────

    // Callbacks registered once by WorkItemCountsService.
    // null = no callback registered yet → gauge emits nothing.
    private static Func<IEnumerable<Measurement<long>>>? _activeJobsCallback;
    private static Func<IEnumerable<Measurement<long>>>? _totalConnectionsCallback;

    /// <summary>
    /// Registers callbacks that supply <c>agent.jobs.active</c> and <c>agent.connections.total</c>
    /// gauge measurements.
    /// Called once at startup by <c>WorkItemCountsService</c> on the Scheduler leader.
    /// The callbacks are invoked on every OTel collection cycle; they should return an empty
    /// enumerable when the leader has no data yet (before first poll) or is not the leader.
    /// Logs a warning if called more than once (e.g. from a misconfigured DI container or
    /// parallel test runs) — the first registration wins and subsequent calls are ignored.
    /// </summary>
    public static void RegisterAgentGaugeCallbacks(
        Func<IEnumerable<Measurement<long>>> activeJobsCallback,
        Func<IEnumerable<Measurement<long>>> totalConnectionsCallback)
    {
        ArgumentNullException.ThrowIfNull(activeJobsCallback);
        ArgumentNullException.ThrowIfNull(totalConnectionsCallback);

        // Use Interlocked.CompareExchange so the first registration wins and a second call
        // cannot silently replace the live gauge callbacks. The guards are applied independently
        // to each field (matching the issue's guidance); cross-field atomicity is not needed
        // because there is exactly one production call site (WorkItemCountsService constructor).
        var priorActiveJobs = Interlocked.CompareExchange(ref _activeJobsCallback, activeJobsCallback, null);
        var priorTotalConns = Interlocked.CompareExchange(ref _totalConnectionsCallback, totalConnectionsCallback, null);

        if (priorActiveJobs is not null || priorTotalConns is not null)
        {
            Serilog.Log.Warning(
                "PipelineTelemetry: RegisterAgentGaugeCallbacks called more than once — " +
                "previous callbacks remain active. This indicates a DI misconfiguration or parallel test run.");
            // First registration wins in the common case (both fields null on entry).
            // In the pathological partial-state (one field null, one non-null from an interrupted
            // prior call), the CAS for the null field will succeed and replace it — but this
            // scenario is unreachable in production (single call site, single DI instance).
        }
    }
}
