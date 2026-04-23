namespace KiroWebUI.Pipeline.Models;

public enum PipelineStep
{
    Created,
    CloningRepository,
    CreatingBranch,
    AnalyzingCode,
    PostingAnalysis,
    GeneratingCode,
    ReviewingCode,
    RunningQualityGates,
    CreatingPullRequest,
    Completed,
    Failed,
    Cancelled
}
