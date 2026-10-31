using System.Diagnostics.Metrics;

namespace CodingAgent.Pipeline.Telemetry;

public static partial class PipelineTelemetry
{
    // ── Brain review metrics ─────────────────────────────────────────────────────

    /// <summary>
    /// Counter: skill lines a brain review promoted, appended, retired or marked missed again.
    /// Recorded once per skill line in each brain review. Recorded by: API.
    /// Tags: action (promoted / appended / retired / missed-again), scope (repository / technology / general / other).
    /// </summary>
    public static readonly Counter<long> BrainReviewLines;

    /// <summary>
    /// Counter: brain reviews refused by validation per consolidation run.
    /// Recorded once per refusal. Recorded by: API.
    /// Tags: reason (the refusal reason string, or "other" for unknown values).
    /// </summary>
    public static readonly Counter<long> BrainReviewRefusals;
}
