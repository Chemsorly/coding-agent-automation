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
            // PrMerged: PR was merged while the run was active — work is done, run ends Succeeded.
            PipelineStep.PrMerged => WorkItemStatus.Succeeded,
            // PrClosed: PR was closed without merge — run ends Cancelled (no label:error).
            PipelineStep.PrClosed => WorkItemStatus.Cancelled,
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

    /// <summary>
    /// Resolves the agent label for a terminal run outcome.
    /// </summary>
    /// <remarks>
    /// Returns <paramref name="finalLabel"/> when it is a known agent label (present in
    /// <see cref="AgentLabels.All"/>); otherwise falls back to the outcome-based mapping:
    /// <list type="bullet">
    ///   <item><see cref="WorkItemStatus.Succeeded"/> → <see cref="AgentLabels.Done"/></item>
    ///   <item><see cref="WorkItemStatus.Failed"/> → <see cref="AgentLabels.Error"/></item>
    ///   <item><see cref="WorkItemStatus.Cancelled"/> → <see cref="AgentLabels.Cancelled"/></item>
    /// </list>
    /// Returns <c>null</c> for any other status value.
    /// <para>
    /// This centralises the "honour FinalLabel iff it is in AgentLabels.All, else map the terminal
    /// outcome to Done/Error/Cancelled" pattern that was previously duplicated across
    /// <c>RunLifecycleManager.FailRunCoreAsync</c>, <c>RunLifecycleManager.CompleteRunAsync</c>,
    /// and <c>AgentJobLifecycleService.SwapLabelAndPostCommentAsync</c> (issue #3261).
    /// </para>
    /// <para>
    /// Note: <see cref="AgentLabels.All"/> is used (not <see cref="AgentLabels.SwapTargets"/>) to
    /// preserve existing behaviour at all call sites. Migrating to <c>SwapTargets</c> (which
    /// excludes <c>agent:generated</c>) should be tracked as a separate issue.
    /// </para>
    /// </remarks>
    /// <param name="terminalStatus">The terminal <see cref="WorkItemStatus"/> for the run.</param>
    /// <param name="finalLabel">
    /// An optional label override set by the agent or pipeline (e.g. <c>agent:needs-refinement</c>).
    /// Honoured only when it is a member of <see cref="AgentLabels.All"/>.
    /// </param>
    /// <returns>The resolved agent label string, or <c>null</c> when <paramref name="terminalStatus"/> is unrecognised.</returns>
    public static string? ResolveAgentLabel(WorkItemStatus terminalStatus, string? finalLabel)
    {
        if (finalLabel is not null && AgentLabels.All.Contains(finalLabel))
            return finalLabel;

        return terminalStatus switch
        {
            WorkItemStatus.Succeeded => AgentLabels.Done,
            WorkItemStatus.Failed => AgentLabels.Error,
            WorkItemStatus.Cancelled => AgentLabels.Cancelled,
            _ => null
        };
    }
}
