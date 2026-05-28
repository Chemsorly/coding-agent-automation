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
    /// Uses <see cref="Interlocked.Exchange(ref int, int)"/> for the three code review count
    /// fields to preserve thread-safe update semantics.
    /// </summary>
    public static void Apply(PipelineRun run, JobCompletionPayload payload)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(payload);

        run.CurrentStep = payload.FinalStep;
        run.CompletedAt = payload.CompletedAt.UtcDateTime;
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
        Interlocked.Exchange(ref run.CodeReviewCriticalCount, payload.CodeReviewCriticalCount);
        Interlocked.Exchange(ref run.CodeReviewWarningCount, payload.CodeReviewWarningCount);
        Interlocked.Exchange(ref run.CodeReviewSuggestionCount, payload.CodeReviewSuggestionCount);
        run.Feedback = payload.Feedback;
        run.TotalTokens = payload.TotalTokens;
        run.TotalCost = payload.TotalCost;
    }
}
