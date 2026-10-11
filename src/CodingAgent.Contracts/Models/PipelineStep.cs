namespace CodingAgent.Pipeline.Models;

/// <summary>
/// Represents the current step of a pipeline run.
/// <para>
/// ⚠️ WIRE/DB CONTRACT: These ordinal values are serialized as integers over MessagePack
/// (SignalR wire protocol) in ActiveJobState.CurrentStep, HeartbeatMessage.CurrentStep,
/// and JobCompletionPayload.FinalStep. Do NOT reorder, rename with different values,
/// or insert new members mid-enum. Always append new members at the end with the next
/// sequential value.
/// </para>
/// </summary>
public enum PipelineStep
{
    Created = 0,
    CloningRepository = 1,
    SyncingBrainRepoPreRun = 2,
    CreatingBranch = 3,
    VerifyingBaseline = 4,
    AnalyzingCode = 5,
    ReviewingAnalysis = 6,
    PostingAnalysis = 7,
    GeneratingCode = 8,
    ReviewingCode = 9,
    RunningQualityGates = 10,
    PreparingForPullRequest = 11,
    FinalizingPullRequest = 12,
    // GeneratingPrDescription = 13 intentionally removed — ordinal 13 is vacant; do not reuse
    ReflectingOnRun = 14,
    SyncingBrainRepoPostRun = 15,
    Completed = 16,
    Failed = 17,
    Cancelled = 18,
    ExtractingLinkedIssues = 19,
    PostingFindings = 20,
    DownloadingOpenIssues = 21,
    ExploringCodebase = 22,
    GeneratingPlan = 23,
    ReviewingPlan = 24,
    PostingPlan = 25,
    GeneratingSubIssues = 26,
    CreatingIssues = 27,
    PostingSummary = 28,
    RunningEnvironmentSetup = 29,

    /// <summary>
    /// Terminal-like step: PR was conflicted with main during CI wait.
    /// Pipeline restarted automatically via <c>agent:next</c> label swap.
    /// No human action required — re-dispatched run will rebase and re-enter CI.
    /// </summary>
    ConflictRestart = 30,

    /// <summary>
    /// Terminal step: PR was merged while the run was active (during CI polling or at run start).
    /// Run ends as <see cref="WorkItemStatus.Succeeded"/> — the work is already done.
    /// No further commits pushed, no LLM invocations.
    /// </summary>
    PrMerged = 31,

    /// <summary>
    /// Terminal step: PR was closed without merging while the run was active (during CI polling or at run start).
    /// Run ends as <see cref="WorkItemStatus.Cancelled"/>.
    /// </summary>
    PrClosed = 32,

    /// <summary>
    /// Post-codegen check step: verifies that identifiers the branch had added in force-resolved
    /// conflict files were re-applied during code generation (issue #3435).
    /// Non-blocking: always returns Continue. Runs after GeneratingCode, before ReviewingCode.
    /// </summary>
    CheckingDroppedIdentifiers = 33,

    // 34–46 are reserved for the consolidation progress steps of #3566; gaps are fine.

    /// <summary>Triage: the agent investigates the reported problem and writes its result.</summary>
    Investigating = 47,

    /// <summary>Triage: the adversarial review checks the root cause analysis and the drafts.</summary>
    ReviewingRca = 48,

    /// <summary>Triage: the result is reported to the API and, for a tracker issue, posted as a comment.</summary>
    ReportingRca = 49
}
