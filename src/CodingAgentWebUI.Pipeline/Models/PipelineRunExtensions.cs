namespace CodingAgentWebUI.Pipeline.Models;

public static class PipelineRunExtensions
{
    /// <summary>
    /// Infers the last pipeline step that was reached before a terminal state.
    /// Used by UI components to determine which step to mark as failed/cancelled.
    /// </summary>
    public static PipelineStep GetLastReachedStep(this PipelineRun run)
    {
        if (!string.IsNullOrEmpty(run.PullRequestUrl)) return PipelineStep.CreatingPullRequest;
        if (run.HighWaterMark >= PipelineStep.PreparingForPullRequest && run.LatestQualityReport is not null) return PipelineStep.PreparingForPullRequest;
        if (run.LatestQualityReport is not null) return PipelineStep.RunningQualityGates;
        if (run.CodeReviewIterationsCompleted > 0) return PipelineStep.ReviewingCode;
        if (run.FilesChangedCount > 0 || run.ChatHistory.Count > 0) return PipelineStep.GeneratingCode;
        if (run.AnalysisContent is not null) return PipelineStep.PostingAnalysis;
        if (run.HighWaterMark >= PipelineStep.AnalyzingCode) return PipelineStep.AnalyzingCode;
        if (run.BaselineHealthPassed is not null) return PipelineStep.VerifyingBaseline;
        if (!string.IsNullOrEmpty(run.BranchName)) return PipelineStep.CreatingBranch;
        if (run.HighWaterMark >= PipelineStep.SyncingBrainRepoPreRun) return PipelineStep.SyncingBrainRepoPreRun;
        if (!string.IsNullOrEmpty(run.WorkspacePath)) return PipelineStep.CloningRepository;
        return PipelineStep.Created;
    }

    /// <summary>
    /// Accumulates token usage and cost from an agent result into the pipeline run totals.
    /// </summary>
    public static void AccumulateTokenUsage(this PipelineRun run, AgentResult? result)
    {
        if (result?.Usage is null) return;
        run.TotalTokens += result.Usage.TotalTokens;
        if (result.Cost is not null)
            run.TotalCost = (run.TotalCost ?? 0m) + result.Cost.Value;
    }
}
