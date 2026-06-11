namespace CodingAgentWebUI.Pipeline.Models;

public static class PipelineStepExtensions
{
    public static string ToDisplayName(this PipelineStep step) => step switch
    {
        PipelineStep.Created => "Pipeline Created",
        PipelineStep.CloningRepository => "Cloning Repository",
        PipelineStep.RunningEnvironmentSetup => "Environment Setup",
        PipelineStep.SyncingBrainRepoPreRun => "Loading Brain Context",
        PipelineStep.CreatingBranch => "Creating Branch",
        PipelineStep.VerifyingBaseline => "Verifying Baseline",
        PipelineStep.AnalyzingCode => "Analyzing Code",
        PipelineStep.ReviewingAnalysis => "Reviewing Analysis",
        PipelineStep.PostingAnalysis => "Posting Analysis",
        PipelineStep.GeneratingCode => "Generating Code",
        PipelineStep.ReviewingCode => "Reviewing Code",
        PipelineStep.RunningQualityGates => "Running Quality Gates",
        PipelineStep.ExtractingLinkedIssues => "Extracting Linked Issues",
        PipelineStep.PostingFindings => "Posting Review Findings",
        PipelineStep.PreparingForPullRequest => "Preparing for Pull Request",
        PipelineStep.CreatingPullRequest => "Creating Pull Request",
        PipelineStep.GeneratingPrDescription => "Generating PR Description",
        PipelineStep.ReflectingOnRun => "Reflecting on Run",
        PipelineStep.SyncingBrainRepoPostRun => "Saving Brain Knowledge",
        PipelineStep.DownloadingOpenIssues => "Downloading Issues",
        PipelineStep.ExploringCodebase => "Exploring Codebase",
        PipelineStep.GeneratingPlan => "Generating Plan",
        PipelineStep.ReviewingPlan => "Reviewing Plan",
        PipelineStep.PostingPlan => "Posting Plan",
        PipelineStep.GeneratingSubIssues => "Generating Sub-Issues",
        PipelineStep.CreatingIssues => "Creating Issues",
        PipelineStep.PostingSummary => "Posting Summary",
        PipelineStep.Completed => "Completed",
        PipelineStep.Failed => "Failed",
        PipelineStep.Cancelled => "Cancelled",
        _ => step.ToString()
    };
}
