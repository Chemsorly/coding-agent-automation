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
    WaitingForChat,
    RunningQualityGates,
    CreatingPullRequest,
    Completed,
    Failed,
    Cancelled
}
