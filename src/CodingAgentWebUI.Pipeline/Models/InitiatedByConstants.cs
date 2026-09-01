namespace CodingAgentWebUI.Pipeline.Models;

/// <summary>
/// Well-known <c>InitiatedBy</c> string values recorded on <see cref="PipelineRun"/> and
/// <see cref="JobDistributionRequest"/> to identify the source that triggered a run.
/// </summary>
/// <remarks>
/// Format conventions:
/// <list type="bullet">
///   <item><c>loop:*</c>  — triggered by an automated dispatch-loop cycle</item>
///   <item><c>manual</c>  — triggered by a human via the UI</item>
///   <item><c>consolidation:*</c>  — consolidation runs (UI or automatic)</item>
///   <item><c>rehydrated</c>  — value was lost; run was re-created from a restarted orchestrator</item>
/// </list>
/// </remarks>
public static class InitiatedByConstants
{
    // ── Automated dispatch loop ──────────────────────────────────────────────

    /// <summary>Issue-implementation run dispatched by the main polling loop.</summary>
    public const string LoopIssue = "loop:issue";

    /// <summary>PR-review run dispatched by the main polling loop.</summary>
    public const string LoopReview = "loop:review";

    /// <summary>Epic-decomposition run dispatched by the main polling loop.</summary>
    public const string LoopDecomposition = "loop:decomposition";

    /// <summary>
    /// Rework run dispatched after the housekeeping loop detected a conflicted PR and
    /// swapped the linked issue back to <c>agent:next</c>.
    /// </summary>
    public const string LoopRework = "loop:rework";

    // ── Manual (human-initiated via UI) ──────────────────────────────────────

    /// <summary>Run dispatched manually from a UI drawer (issue, PR review, or epic).</summary>
    public const string Manual = "manual";

    // ── Consolidation ────────────────────────────────────────────────────────

    /// <summary>Consolidation run triggered by the user via the Consolidation page.</summary>
    public const string ConsolidationManual = "consolidation:manual";

    /// <summary>Consolidation run triggered automatically by the consolidation dispatch loop.</summary>
    public const string ConsolidationAuto = "consolidation:auto";

    // ── Fallback / internal ──────────────────────────────────────────────────

    /// <summary>
    /// Fallback used when a run is re-created from a restarted orchestrator and the original
    /// <c>InitiatedBy</c> value could not be recovered.
    /// </summary>
    public const string Rehydrated = "rehydrated";

    // ── Helpers ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Returns <see langword="true"/> when the value originates from a manual human dispatch
    /// (i.e. starts with <c>"manual"</c>).  Used for priority-weight assignment.
    /// </summary>
    public static bool IsManual(string? initiatedBy) =>
        initiatedBy is not null &&
        initiatedBy.StartsWith("manual", StringComparison.Ordinal);
}
