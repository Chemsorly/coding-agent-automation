namespace KiroWebUI.Pipeline.Models;

public enum PipelineStep
{
    Created,
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
