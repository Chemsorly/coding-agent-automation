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
        run.MarkCompleted(payload.CompletedAt);
        run.FailureReason = payload.FailureReason;
        // Only overwrite PullRequestUrl when the payload carries a value.
        // For terminal paths like ConflictRestart the agent sends PullRequestUrl = null because it
        // did not create a PR in that run — but PullRequestOrchestrator may have already set the
        // URL on the run object in a prior step (e.g. the PR already existed and was conflicted).
        // Unconditionally assigning null would erase the URL and prevent it from being persisted
        // via ToSummary → history, making the PR link disappear from the Run page.
        if (payload.PullRequestUrl != null)
            run.PullRequestUrl = payload.PullRequestUrl;
        // TODO: [WARNING] PullRequestNumber and IsDraftPr are unconditionally overwritten from the
        // payload even when PullRequestUrl is preserved from a prior step. On a ConflictRestart path
        // the payload has PullRequestNumber = 0 (default), silently resetting the run's existing PR
        // number while the URL is still preserved — creating an inconsistent state (URL present,
        // number absent). Apply the same null/default guard as PullRequestUrl: only overwrite
        // PullRequestNumber when payload.PullRequestNumber != 0 (or use a nullable field).
        // (Correctness + DotNetSpecialist review, issue #2947)
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
    }
}
