using CodingAgentWebUI.Pipeline.Telemetry;

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
        if (run.HighWaterMark >= PipelineStep.ReviewingAnalysis) return PipelineStep.ReviewingAnalysis;
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
    public static void AccumulateTokenUsage(this PipelineRun run, AgentResult? result, string? phase = null)
    {
        if (result?.Usage is null) return;
        run.TotalTokens += result.Usage.TotalTokens;

        var tags = phase is null
            ? PipelineTelemetry.BuildTags(run.RunType, run.ProjectId, run.ProjectName)
            : PipelineTelemetry.BuildTagsWithPhase(run.RunType, run.ProjectId, run.ProjectName, phase);

        if (result.Cost is not null)
        {
            run.TotalCost = (run.TotalCost ?? 0m) + result.Cost.Value;
            PipelineTelemetry.CostUsd.Add((double)result.Cost.Value, tags);
        }

        PipelineTelemetry.TokensUsed.Add(result.Usage.TotalTokens, tags);

        if (phase is not null)
        {
            // TODO: (existing.Cost ?? 0m) + (result.Cost ?? 0m) coerces null+null to 0m, losing "no cost data" semantic on update.
            // Consider: existing.Cost is null && result.Cost is null ? null : (existing.Cost ?? 0m) + (result.Cost ?? 0m)
            run.Metrics.PhaseBreakdown.AddOrUpdate(phase,
                new PhaseUsage(result.Usage.TotalTokens, result.Cost),
                (_, existing) => new PhaseUsage(
                    existing.Tokens + result.Usage.TotalTokens,
                    (existing.Cost ?? 0m) + (result.Cost ?? 0m)));
        }
    }
}
