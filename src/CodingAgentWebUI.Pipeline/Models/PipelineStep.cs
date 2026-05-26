namespace CodingAgentWebUI.Pipeline.Models;

public enum PipelineStep
{
    Created,
    CloningRepository,
    SyncingBrainRepoPreRun,
    CreatingBranch,
    VerifyingBaseline,
    AnalyzingCode,
    ReviewingAnalysis,
    PostingAnalysis,
    GeneratingCode,
    ReviewingCode,
    RunningQualityGates,
    PreparingForPullRequest,
    CreatingPullRequest,
    ReflectingOnRun,
    SyncingBrainRepoPostRun,
    Completed,
    Failed,
    Cancelled,
    ExtractingLinkedIssues,
    PostingFindings,
    DownloadingOpenIssues,
    ExploringCodebase,
    GeneratingPlan,
    ReviewingPlan,
    PostingPlan,
    GeneratingSubIssues,
    CreatingIssues,
    PostingSummary
}
