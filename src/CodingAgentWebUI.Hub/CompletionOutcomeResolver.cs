using CodingAgentWebUI.Pipeline.Models;

namespace CodingAgentWebUI.Hub;

/// <summary>
/// Maps a <see cref="PipelineStep"/> to a terminal <see cref="WorkItemStatus"/> and derives the
/// corresponding error message and <see cref="FailureReason"/> from the completion payload.
/// </summary>
/// <remarks>
/// Extracted from <see cref="AgentJobLifecycleService"/> to eliminate four identical inline
/// switch expressions and conditional derivations across the consolidation, regular, defensive-cleanup,
/// and orphaned completion paths.
/// </remarks>
internal static class CompletionOutcomeResolver
{
    /// <summary>
    /// Resolves the terminal outcome for a completed job.
    /// </summary>
    /// <param name="finalStep">The pipeline step reported by the agent.</param>
    /// <param name="failureReason">
    /// The human-readable failure reason string from the run or payload.
    /// Used as the primary error message when the outcome is <see cref="WorkItemStatus.Failed"/>.
    /// </param>
    /// <param name="failureCategory">
    /// The structured failure category from the payload.
    /// Defaults to <see cref="FailureReason.AgentError"/> when null and the outcome is Failed.
    /// </param>
    /// <param name="failureFallback">
    /// Site-specific fallback string used as the error message when <paramref name="failureReason"/> is null
    /// and the outcome is Failed. Each call site passes a distinct string so operators can identify
    /// which code path produced the error.
    /// </param>
    /// <returns>
    /// A tuple of (<see cref="WorkItemStatus"/>, error message or null, <see cref="FailureReason"/> or null).
    /// Error message and failure reason are non-null only when the status is <see cref="WorkItemStatus.Failed"/>.
    /// </returns>
    public static (WorkItemStatus Status, string? ErrorMsg, FailureReason? FailureReason)
        Resolve(PipelineStep finalStep, string? failureReason, FailureReason? failureCategory, string failureFallback)
    {
        // TODO [WARNING]: PipelineStep.ConflictRestart falls through to the default arm and is mapped
        // to WorkItemStatus.Failed. This causes the WorkItem to record an errorMsg and FailureReason
        // (defaulting to FailureReason.AgentError when FailureCategory is null) and sets activity tag
        // "success=false", misattributing an automatic infrastructure recovery as an agent failure in
        // metrics and WorkItem error fields. Consider adding an explicit ConflictRestart arm (e.g.
        // mapping to a non-Failed terminal status, or at minimum a distinct FailureReason such as
        // FailureReason.ConflictRestart) so that conflict-restart runs are not classified as agent errors.
        // Note: the re-queue itself is unaffected — PostCompletionBookkeepingAsync reads FinalLabel
        // (agent:next) and swaps the issue label correctly regardless of WorkItemStatus. (#2359)
        var status = finalStep switch
        {
            PipelineStep.Completed => WorkItemStatus.Succeeded,
            PipelineStep.Cancelled => WorkItemStatus.Cancelled,
            _ => WorkItemStatus.Failed
        };

        var errorMsg = status == WorkItemStatus.Failed
            ? failureReason ?? failureFallback
            : null;

        var failureEnum = status == WorkItemStatus.Failed
            ? failureCategory ?? FailureReason.AgentError
            : (FailureReason?)null;

        return (status, errorMsg, failureEnum);
    }
}
