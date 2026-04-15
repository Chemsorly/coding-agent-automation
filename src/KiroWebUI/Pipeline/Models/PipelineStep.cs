namespace KiroWebUI.Pipeline.Models;

public enum PipelineStep
{
    Created,
    PostingAnalysis,
    CloningRepository,
    CreatingBranch,
    GeneratingCode,
    WaitingForChat,
    RunningQualityGates,
    CreatingPullRequest,
    Completed,
    Failed,
    Cancelled
}
