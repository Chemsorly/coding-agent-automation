using CodingAgent.Pipeline.Models;

namespace CodingAgent.Pipeline;

/// <summary>
/// Maps a <see cref="PipelineStep"/> to a terminal <see cref="WorkItemStatus"/> and derives the
/// corresponding error message and <see cref="FailureReason"/> from the completion payload.
/// </summary>
/// <remarks>
/// Shared between <c>CodingAgent.Agent</c> (HTTP primary completion path) and
/// <c>CodingAgent.AgentGateway</c> (SignalR secondary completion path) so that both
/// channels apply identical outcome mapping. Previously lived in
/// <c>CodingAgent.AgentGateway</c> only, which caused <c>ConflictRestart</c> to be
/// recorded as <c>Failed/AgentError</c> on the HTTP path (issue #2956).
/// </remarks>
public static class CompletionOutcomeResolver
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
        // TODO: [WARNING] failureFallback is non-nullable but has no ArgumentNullException.ThrowIfNull guard.
        // This class was promoted from internal (CodingAgent.AgentGateway) to public (CodingAgent.Pipeline)
        // as part of issue #2956, making it a cross-assembly API. A caller passing null for failureFallback
        // when status == Failed and failureReason is also null produces a null ErrorMessage with no diagnostic.
        // All current call sites pass string literals, so no regression today, but add:
        //   ArgumentNullException.ThrowIfNull(failureFallback);
        // as per the documented convention for public method parameters. (Review finding: DotNetSpecialist [WARNING])
        var status = finalStep switch
        {
            PipelineStep.Completed => WorkItemStatus.Succeeded,
            PipelineStep.Cancelled => WorkItemStatus.Cancelled,
            // ConflictRestart is a clean auto-recovery termination, not a failure.
            // The pipeline re-queues the issue via FinalLabel = agent:next; no human action required.
            PipelineStep.ConflictRestart => WorkItemStatus.Succeeded,
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
