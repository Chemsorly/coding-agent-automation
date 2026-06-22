using CodingAgentWebUI.Pipeline.Models;

namespace CodingAgentWebUI.Hubs;

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
        run.PullRequestUrl = payload.PullRequestUrl;
        run.PullRequestNumber = payload.PullRequestNumber;
        run.IsDraftPr = payload.IsDraftPr;
        run.RetryCount = payload.RetryCount;
        run.FilesChangedCount = payload.FilesChangedCount;
        run.LinesAdded = payload.LinesAdded;
        run.LinesRemoved = payload.LinesRemoved;
        run.BrainUpdatesPushed = payload.BrainUpdatesPushed;
        run.AnalysisRecommendation = payload.AnalysisRecommendation;
        run.AnalysisConcerns = payload.AnalysisConcerns;
        run.AnalysisBlockingIssues = payload.AnalysisBlockingIssues;
        run.BlacklistedFilesDetected = payload.BlacklistedFilesDetected;
        run.CodeReviewAgentsRun = payload.CodeReviewAgentsRun;
        run.SetCodeReviewCounts(payload.CodeReviewCriticalCount, payload.CodeReviewWarningCount, payload.CodeReviewSuggestionCount);
        run.Feedback = payload.Feedback;
        run.TotalTokens = payload.TotalTokens;
        run.TotalCost = payload.TotalCost;
    }
}
