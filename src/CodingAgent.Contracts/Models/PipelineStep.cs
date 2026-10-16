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

    /// <summary>Refactoring scan: git hotspot analysis.</summary>
    AnalyzingHotspots = 34,

    /// <summary>Refactoring scan: open issues and past scan issues.</summary>
    ReadingIssueContext = 35,

    /// <summary>Refactoring scan: phase 0 convention extraction.</summary>
    ExtractingConventions = 36,

    /// <summary>Refactoring scan: phase 1, three detectors in parallel.</summary>
    DetectingFindings = 37,

    /// <summary>Refactoring scan: phase 2 proposal aggregation.</summary>
    AggregatingProposals = 38,

    /// <summary>Refactoring scan: adversarial review and refinement.</summary>
    ReviewingProposals = 39,

    /// <summary>Refactoring scan: validation and issue creation.</summary>
    CreatingRefactoringIssues = 40,

    /// <summary>Brain consolidation: the consolidation agent.</summary>
    ConsolidatingKnowledge = 41,

    /// <summary>Brain consolidation: diff summary, adversarial review, refinement.</summary>
    ReviewingBrainChanges = 42,

    /// <summary>Brain consolidation: commit and push to the brain's base branch.</summary>
    PushingBrainChanges = 43,

    // 44–46 remain vacant within the consolidation reservation range.

    /// <summary>Triage: the agent investigates the reported problem and writes its result.</summary>
    Investigating = 47,

    /// <summary>Triage: the adversarial review checks the root cause analysis and the drafts.</summary>
    ReviewingRca = 48,

    /// <summary>Triage: the result is reported to the API and, for a tracker issue, posted as a comment.</summary>
    ReportingRca = 49
}
