namespace CodingAgentWebUI.Pipeline.Models;

/// <summary>
/// Represents a discrete step in the pipeline execution lifecycle.
/// <para>
/// ⚠️ WIRE/DB CONTRACT: Ordinal values are serialized as integers over MessagePack (SignalR wire protocol)
/// in <c>ActiveJobState.CurrentStep</c>, <c>HeartbeatMessage.CurrentStep</c>, and <c>JobCompletionPayload.FinalStep</c>.
/// Do NOT reorder, rename, or insert members mid-enum — append new members at the end with the next sequential value.
/// Changing an existing ordinal silently breaks wire compatibility between orchestrator and agents.
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
