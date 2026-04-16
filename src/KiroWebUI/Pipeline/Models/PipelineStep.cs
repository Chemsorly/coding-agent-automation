namespace KiroWebUI.Pipeline.Models;

public enum PipelineStep
{
    Created,
    CloningRepository,
    CreatingBranch,
    AnalyzingCode,
    PostingAnalysis,
    WaitingForAnalysisApproval,
    GeneratingCode,
    ReviewingCode,
    WaitingForChat,
    RunningQualityGates,
    CreatingPullRequest,
    Completed,
    Failed,
    Cancelled
}
