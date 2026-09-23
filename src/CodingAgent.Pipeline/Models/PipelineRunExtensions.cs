using CodingAgent.Pipeline.Telemetry;

namespace CodingAgent.Pipeline.Models;

public static class PipelineRunExtensions
{
    /// <summary>
    /// Infers the last pipeline step that was reached before a terminal state.
    /// Used by UI components to determine which step to mark as failed/cancelled.
    /// When the run was seeded from a persisted summary (not a live snapshot), the in-memory
    /// fields (FilesChangedCount, LatestQualityReport, etc.) are all zero/null. In that case
    /// HighWaterMark — populated from <see cref="PipelineRunSummary.LastActiveStep"/> — is used
    /// as the direct authoritative answer rather than a fallback ordinal heuristic.
    /// </summary>
    public static PipelineStep GetLastReachedStep(this PipelineRun run)
    {
        if (!string.IsNullOrEmpty(run.PullRequestUrl)) return PipelineStep.FinalizingPullRequest;
        if (run.HighWaterMark >= PipelineStep.PreparingForPullRequest && run.LatestQualityReport is not null) return PipelineStep.PreparingForPullRequest;
        if (run.LatestQualityReport is not null) return PipelineStep.RunningQualityGates;
        if (run.CodeReviewIterationsCompleted > 0) return PipelineStep.ReviewingCode;
        if (run.FilesChangedCount > 0 || run.ChatHistory.Count > 0) return PipelineStep.GeneratingCode;
        if (run.AnalysisContent is not null) return PipelineStep.PostingAnalysis;
        // When specific data fields are absent (e.g. summary-seeded model), use HighWaterMark as
        // the authoritative last step — it holds the value persisted from PipelineRun.HighWaterMark
        // via PipelineRunSummary.LastActiveStep. Skip terminal and Created (ordinal 0) values.
        // TODO: [WARNING] For live runs receiving partial hub snapshots, HighWaterMark may lag behind
        // data-field evidence: e.g. BranchName is already set (CreatingBranch reached) but HighWaterMark
        // is still CloningRepository. The pre-diff code refined the step via BranchName/WorkspacePath
        // checks *after* the HighWaterMark heuristic; placing the HighWaterMark block here causes the
        // sidebar to regress to an earlier step on live runs when HighWaterMark hasn't caught up.
        // For summary-seeded (terminal) runs this is correct; for live runs consider keeping the
        // BranchName/WorkspacePath checks before this block as a refinement layer.
        if (run.HighWaterMark is not PipelineStep.Created
            and not PipelineStep.Failed
            and not PipelineStep.Cancelled
            and not PipelineStep.Completed
            and not PipelineStep.ConflictRestart)
            return run.HighWaterMark;
        if (!string.IsNullOrEmpty(run.BranchName)) return PipelineStep.CreatingBranch;
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
        run.CacheReadTokens += result.Usage.CacheReadTokens;
        run.CacheWriteTokens += result.Usage.CacheWriteTokens;

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
            run.Metrics.PhaseBreakdown.AddOrUpdate(phase,
                new PhaseUsage(result.Usage.TotalTokens, result.Cost),
                (_, existing) => new PhaseUsage(
                    existing.Tokens + result.Usage.TotalTokens,
                    existing.Cost is null && result.Cost is null ? null : (existing.Cost ?? 0m) + (result.Cost ?? 0m)));
        }
    }

    /// <summary>
    /// Accumulates token usage from a <see cref="TokenUsage"/> object directly into the pipeline run totals.
    /// Use this overload when only a <see cref="TokenUsage"/> is available (e.g. from
    /// <see cref="CodingAgent.Pipeline.Services.AdversarialReviewResult.ReviewTokenUsage"/>),
    /// rather than a full <see cref="AgentResult"/>.
    /// Cost is not available in this overload — only token counts are accumulated.
    /// </summary>
    public static void AccumulateTokenUsage(this PipelineRun run, TokenUsage? usage, string? phase = null)
    {
        if (usage is null) return;
        run.TotalTokens += usage.TotalTokens;
        run.CacheReadTokens += usage.CacheReadTokens;
        run.CacheWriteTokens += usage.CacheWriteTokens;

        var tags = phase is null
            ? PipelineTelemetry.BuildTags(run.RunType, run.ProjectId, run.ProjectName)
            : PipelineTelemetry.BuildTagsWithPhase(run.RunType, run.ProjectId, run.ProjectName, phase);

        PipelineTelemetry.TokensUsed.Add(usage.TotalTokens, tags);

        if (phase is not null)
        {
            run.Metrics.PhaseBreakdown.AddOrUpdate(phase,
                new PhaseUsage(usage.TotalTokens, null),
                (_, existing) => new PhaseUsage(existing.Tokens + usage.TotalTokens, existing.Cost));
        }
    }
}
