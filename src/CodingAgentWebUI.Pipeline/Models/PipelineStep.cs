namespace CodingAgentWebUI.Pipeline.Models;

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
    CreatingPullRequest = 12,
    GeneratingPrDescription = 13,
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
    RunningEnvironmentSetup = 29
}
