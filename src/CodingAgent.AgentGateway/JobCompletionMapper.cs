using CodingAgent.Pipeline.Models;

namespace CodingAgent.AgentGateway;

/// <summary>
/// Maps completion data from <see cref="JobCompletionPayload"/> onto a <see cref="PipelineRun"/>.
/// Extracted from <see cref="AgentHub.ReportJobCompleted"/> to separate the mapping concern
/// from hub communication and make it independently testable.
/// </summary>
internal static class JobCompletionMapper
{
    /// <summary>
    /// Applies all property mappings from <paramref name="payload"/> to <paramref name="run"/>.
    /// Uses <see cref="PipelineRun.SetCodeReviewCounts"/> for thread-safe update of review counters.
    /// </summary>
    public static void Apply(PipelineRun run, JobCompletionPayload payload)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(payload);

        run.CurrentStep = payload.FinalStep;
        run.SetCompletedAt(payload.CompletedAt);
        run.FailureReason = payload.FailureReason;
        if (payload.FailureCategory is not null)
            run.FailureCategory = payload.FailureCategory;
        // Only overwrite PullRequestUrl when the payload carries a value.
        // For terminal paths like ConflictRestart the agent sends PullRequestUrl = null because it
        // did not create a PR in that run — but PullRequestOrchestrator may have already set the
        // URL on the run object in a prior step (e.g. the PR already existed and was conflicted).
        // Unconditionally assigning null would erase the URL and prevent it from being persisted
        // via ToSummary → history, making the PR link disappear from the Run page.
        if (payload.PullRequestUrl != null)
            run.PullRequestUrl = payload.PullRequestUrl;
        // Only overwrite PullRequestNumber when the payload carries a real value (non-empty),
        // consistent with the null-guard on PullRequestUrl above. This prevents a ConflictRestart
        // payload (PullRequestNumber = null/empty default) from erasing the existing PR number.
        if (!string.IsNullOrEmpty(payload.PullRequestNumber))
            run.PullRequestNumber = payload.PullRequestNumber;
        run.IsDraftPr = payload.IsDraftPr;
        run.RetryCount = payload.RetryCount;
        run.FilesChangedCount = payload.FilesChangedCount;
        run.LinesAdded = payload.LinesAdded;
        run.LinesRemoved = payload.LinesRemoved;
        run.BrainUpdatesPushed = payload.BrainUpdatesPushed;
        run.AnalysisRecommendation = payload.AnalysisRecommendation;
        run.RunMode = payload.RunMode;
        run.AnalysisConcerns = payload.AnalysisConcerns;
        run.AnalysisBlockingIssues = payload.AnalysisBlockingIssues;
        run.BlacklistedFilesDetected = payload.BlacklistedFilesDetected;
        run.CodeReviewAgentsRun = payload.CodeReviewAgentsRun;
        run.SetCodeReviewCounts(payload.CodeReviewCriticalCount, payload.CodeReviewWarningCount, payload.CodeReviewSuggestionCount);
        run.Feedback = payload.Feedback;
        run.TotalTokens = payload.TotalTokens;
        run.TotalCost = payload.TotalCost;
        run.FinalLabel = payload.FinalLabel;
        run.HarnessVersion = payload.HarnessVersion;
        if (payload.PhaseBreakdown is not null)
        {
            run.Metrics.PhaseBreakdown.Clear();
            foreach (var (phase, usage) in payload.PhaseBreakdown)
                run.Metrics.PhaseBreakdown[phase] = ToPhaseUsage(usage);
        }
        if (payload.McpServers is not null)
            run.McpServers = payload.McpServers;

        // Brain v2 fields (spec 054, req 8.1): copy only when the payload carries a value
        if (payload.BrainSkillsResult is not null) run.BrainSkillsResult = payload.BrainSkillsResult;
        if (payload.BrainSkillsBytes is not null) run.BrainSkillsBytes = payload.BrainSkillsBytes;
        if (payload.BrainSkillsCommit is not null) run.BrainSkillsCommit = payload.BrainSkillsCommit;
        if (payload.BrainInboxFile is not null) run.BrainInboxFile = payload.BrainInboxFile;
        if (payload.BrainPushFailed is not null) run.BrainPushFailed = payload.BrainPushFailed;
        if (payload.BrainInboxFileUrl is not null)
        {
            run.BrainInboxFileUrl = CodingAgent.Contracts.Services.AgentLinkSanitizer.HttpsOrNull(payload.BrainInboxFileUrl);
            // Non-null values that don't pass the https check are dropped (req 12.8).
            // The call site in RegularJobCompletionStrategy should log the warning.
        }
    }

    private static PhaseUsage ToPhaseUsage(PhaseUsagePayload p) =>
        new(p.Tokens, p.Cost, p.SessionCount, p.AgentTimeSeconds, p.Provider, p.Model)
        {
            InputTokens = p.InputTokens,
            OutputTokens = p.OutputTokens,
            ReasoningTokens = p.ReasoningTokens,
            CacheReadTokens = p.CacheReadTokens,
            CacheWriteTokens = p.CacheWriteTokens,
            Turns = p.Turns,
            WebSearchRequests = p.WebSearchRequests,
            BillingMode = p.BillingMode,
            ToolCalls = p.ToolCalls,
            UnavailableMcpServers = p.UnavailableMcpServers
        };
}
