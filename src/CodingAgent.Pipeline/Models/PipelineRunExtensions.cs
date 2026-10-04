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
        if (result?.UsageDetails is { RateLimits.Count: > 0 } details)
        {
            foreach (var observation in details.RateLimits)
                run.Metrics.RateLimits[observation.Window] = observation;
        }

        if (result?.Usage is null) return;
        run.TotalTokens += result.Usage.TotalTokens;
        run.CacheReadTokens += result.Usage.CacheReadTokens;
        run.CacheWriteTokens += result.Usage.CacheWriteTokens;

        if (result.Cost is not null)
            run.TotalCost = (run.TotalCost ?? 0m) + result.Cost.Value;

        if (phase is not null)
        {
            run.Metrics.PhaseBreakdown.AddOrUpdate(phase,
                AddUsage(new PhaseUsage(0, null), result.Usage, result.Cost, result.UsageDetails),
                (_, existing) => AddUsage(existing, result.Usage, result.Cost, result.UsageDetails));
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

        if (phase is not null)
        {
            run.Metrics.PhaseBreakdown.AddOrUpdate(phase,
                AddUsage(new PhaseUsage(0, null), usage, cost: null, details: null),
                (_, existing) => AddUsage(existing, usage, cost: null, details: null));
        }
    }

    /// <summary>
    /// Returns <paramref name="phase"/> with one invocation's tokens, cost and usage details added.
    /// </summary>
    private static PhaseUsage AddUsage(PhaseUsage phase, TokenUsage usage, decimal? cost, AgentUsageDetails? details) =>
        phase with
        {
            Tokens = phase.Tokens + usage.TotalTokens,
            Cost = phase.Cost is null && cost is null ? null : (phase.Cost ?? 0m) + (cost ?? 0m),
            InputTokens = phase.InputTokens + usage.InputTokens,
            OutputTokens = phase.OutputTokens + usage.OutputTokens,
            ReasoningTokens = phase.ReasoningTokens + usage.ReasoningTokens,
            CacheReadTokens = phase.CacheReadTokens + usage.CacheReadTokens,
            CacheWriteTokens = phase.CacheWriteTokens + usage.CacheWriteTokens,
            Turns = phase.Turns + (details?.Turns ?? 0),
            WebSearchRequests = phase.WebSearchRequests + (details?.WebSearchRequests ?? 0),
            BillingMode = phase.BillingMode ?? details?.BillingMode
        };

    /// <summary>
    /// Records an agent session (invocation) into the per-phase breakdown.
    /// Updates the session count and elapsed time for the given phase.
    /// </summary>
    /// <param name="run">The pipeline run to update.</param>
    /// <param name="phase">The phase key (e.g. "analysis", "codegen"). Null is a no-op.</param>
    /// <param name="elapsedSeconds">Agent execution duration for this invocation.</param>
    /// <param name="provider">Provider name tag ("kiro", "opencode", etc.).</param>
    /// <param name="model">Model name, or null if unknown.</param>
    public static void AccumulateAgentSession(this PipelineRun run, string? phase, double elapsedSeconds, string? provider = null, string? model = null)
    {
        if (phase is null) return;

        run.Metrics.PhaseBreakdown.AddOrUpdate(phase,
            new PhaseUsage(0, null, 1, elapsedSeconds, provider, model),
            (_, existing) => existing with
            {
                SessionCount = existing.SessionCount + 1,
                AgentTimeSeconds = existing.AgentTimeSeconds + elapsedSeconds,
                // Prefer first non-null provider/model seen (they should all be the same per phase in practice)
                Provider = existing.Provider ?? provider,
                Model = existing.Model ?? model
            });
    }
}
