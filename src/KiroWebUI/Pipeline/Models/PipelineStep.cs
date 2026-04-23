namespace KiroWebUI.Pipeline.Models;

public enum PipelineStep
{
    Created,
    CloningRepository,
    SyncingBrainRepoPreRun,
    CreatingBranch,
    AnalyzingCode,
    PostingAnalysis,
    GeneratingCode,
    ReviewingCode,
    RunningQualityGates,
    CreatingPullRequest,
    SyncingBrainRepoPostRun,
    Completed,
    Failed,
    Cancelled
}
