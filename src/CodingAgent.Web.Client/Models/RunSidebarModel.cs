using CodingAgent.Pipeline.Models;

namespace CodingAgent.Web.Models;

/// <summary>
/// What the run page's pipeline sidebar shows of a run (spec 051 5.0). It is seeded from the run summary and
/// refined by the UI hub's <see cref="RunStateSnapshot"/> and events; the server's live run aggregate stays in
/// the Pipeline project.
/// </summary>
public sealed class RunSidebarModel
{
    public required string RunId { get; init; }
    public required IssueIdentifier IssueIdentifier { get; init; }
    public string IssueTitle { get; init; } = "";
    /// <summary>Set when the run used a brain repository; the sidebar hides the brain steps otherwise.</summary>
    public string? BrainProviderConfigId { get; init; }
    public PipelineRunType RunType { get; init; }

    public PipelineStep CurrentStep { get; set; }
    public PipelineStep HighWaterMark { get; set; }
    public int RetryCount { get; set; }
    public string? BranchName { get; set; }
    public bool? BaselineHealthPassed { get; set; }
    public bool BrainContextLoaded { get; set; }
    public int BrainKnowledgeFileCount { get; set; }
    public IReadOnlyList<string> IssueLabels { get; set; } = [];
    public bool AnalysisSkipped { get; set; }
    public AnalysisGateResult? AnalysisRecommendation { get; set; }
    public int FilesChangedCount { get; set; }
    public int LinesAdded { get; set; }
    public int LinesRemoved { get; set; }
    public int CodeReviewIterationsCompleted { get; set; }
    public int CodeReviewIterationInProgress { get; set; }
    public int CodeReviewIterationsTotal { get; set; }
    public IReadOnlyList<string> CodeReviewAgentsRun { get; set; } = [];
    public int CodeReviewCriticalCount { get; set; }
    public int CodeReviewWarningCount { get; set; }
    public int CodeReviewSuggestionCount { get; set; }
    public QualityGateReport? LatestQualityReport { get; set; }
    public List<QualityGateReport> QualityGateHistory { get; } = [];
    public string? PullRequestUrl { get; set; }
    public string? PullRequestNumber { get; set; }
    public bool IsDraftPr { get; set; }
    public string? ReviewPrUrl { get; set; }
    public IReadOnlyList<string> BlacklistedFilesDetected { get; set; } = [];
    public int OpenIssuesDownloaded { get; set; }
    public int LinkedIssueCount { get; set; }
    public bool BrainUpdatesPushed { get; set; }
    public string? BrainInboxFileUrl { get; set; }
    public string? BrainSkillsResult { get; set; }
    public int BrainSkillsBytes { get; set; }
    public string? BrainSkillsCommit { get; set; }
    public string? BrainInboxFile { get; set; }
    public bool BrainPushFailed { get; set; }
    public int DecompositionSubIssuesCreated { get; set; }
    public int DecompositionSubIssuesAttempted { get; set; }
    public string? FailureReason { get; set; }
    public string? ModelName { get; set; }
    public string? RepositoryName { get; set; }
    public List<ChatEntry> ChatHistory { get; } = [];
    public DateTimeOffset StartedAtOffset { get; set; }
    public DateTimeOffset? CompletedAtOffset { get; set; }
    public RunMode RunMode { get; set; }
    public ConsolidationRunType? ConsolidationType { get; set; }
    public string? ConsolidationTemplateId { get; set; }
    public string? ConsolidationTemplateName { get; set; }
    public Dictionary<string, PhaseUsage> PhaseBreakdown { get; } = [];
    /// <summary>What the refactoring scan produced; null for non-scan runs or while the run is in progress.</summary>
    public RefactoringScanOutcome? RefactoringScanOutcome { get; set; }
    /// <summary>What the brain consolidation changed and pushed; null for non-brain runs or while the run is in progress.</summary>
    public BrainConsolidationOutcome? BrainConsolidationOutcome { get; set; }

    /// <summary>
    /// The last step the run is known to have reached, from the data it produced, else from
    /// <see cref="HighWaterMark"/> (the persisted last active step of a finished run).
    /// </summary>
    public PipelineStep GetLastReachedStep()
    {
        if (!string.IsNullOrEmpty(PullRequestUrl)) return PipelineStep.FinalizingPullRequest;
        if (HighWaterMark >= PipelineStep.PreparingForPullRequest && LatestQualityReport is not null) return PipelineStep.PreparingForPullRequest;
        if (LatestQualityReport is not null) return PipelineStep.RunningQualityGates;
        if (CodeReviewIterationsCompleted > 0) return PipelineStep.ReviewingCode;
        if (FilesChangedCount > 0 || ChatHistory.Count > 0) return PipelineStep.GeneratingCode;
        if (HighWaterMark is not PipelineStep.Created
            and not PipelineStep.Failed
            and not PipelineStep.Cancelled
            and not PipelineStep.Completed
            and not PipelineStep.ConflictRestart)
            return HighWaterMark;
        if (!string.IsNullOrEmpty(BranchName)) return PipelineStep.CreatingBranch;
        return PipelineStep.Created;
    }
}
