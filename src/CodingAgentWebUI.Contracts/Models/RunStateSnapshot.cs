using MessagePack;

namespace CodingAgentWebUI.Pipeline.Models;

/// <summary>
/// Snapshot of live run state pushed by the hub to a newly-subscribing UI client
/// via <c>OnRunStateSnapshot</c>. Sent only to the connecting client (not the whole group),
/// immediately after the output backlog push in <c>SubscribeToRun</c>.
///
/// This DTO carries all fields needed to seed the <c>PipelineSidebar</c> view model,
/// including <see cref="HighWaterMark"/> and <see cref="IssueLabels"/> which are absent
/// from <c>PipelineRunSummary</c>.
///
/// MessagePack serialization is mandatory: the hub uses <c>AddMessagePackProtocol()</c>
/// exclusively. All properties must carry <c>[Key(N)]</c> attributes.
/// </summary>
[MessagePackObject]
public sealed record RunStateSnapshot
{
    [Key(0)]
    public required PipelineStep CurrentStep { get; init; }

    [Key(1)]
    public required PipelineStep HighWaterMark { get; init; }

    [Key(2)]
    public int RetryCount { get; init; }

    [Key(3)]
    public string? BranchName { get; init; }

    [Key(4)]
    public bool? BaselineHealthPassed { get; init; }

    [Key(5)]
    public bool BrainRepoUsed { get; init; }

    [Key(6)]
    public bool BrainContextLoaded { get; init; }

    [Key(7)]
    public int BrainKnowledgeFileCount { get; init; }

    [Key(8)]
    public IReadOnlyList<string> IssueLabels { get; init; } = Array.Empty<string>();

    [Key(9)]
    public bool AnalysisSkipped { get; init; }

    [Key(10)]
    public AnalysisGateResult? AnalysisRecommendation { get; init; }

    [Key(11)]
    public int FilesChangedCount { get; init; }

    [Key(12)]
    public int LinesAdded { get; init; }

    [Key(13)]
    public int LinesRemoved { get; init; }

    [Key(14)]
    public int CodeReviewIterationsCompleted { get; init; }

    [Key(15)]
    public int CodeReviewIterationInProgress { get; init; }

    [Key(16)]
    public int CodeReviewIterationsTotal { get; init; }

    [Key(17)]
    public IReadOnlyList<string> CodeReviewAgentsRun { get; init; } = Array.Empty<string>();

    [Key(18)]
    public int CodeReviewCriticalCount { get; init; }

    [Key(19)]
    public int CodeReviewWarningCount { get; init; }

    [Key(20)]
    public int CodeReviewSuggestionCount { get; init; }

    [Key(21)]
    public QualityGateReport? LatestQualityReport { get; init; }

    [Key(22)]
    public IReadOnlyList<QualityGateReport> QualityGateHistory { get; init; } = Array.Empty<QualityGateReport>();

    [Key(23)]
    public string? PullRequestUrl { get; init; }

    [Key(24)]
    public string? PullRequestNumber { get; init; }

    [Key(25)]
    public bool IsDraftPr { get; init; }

    [Key(26)]
    public IReadOnlyList<string> BlacklistedFilesDetected { get; init; } = Array.Empty<string>();

    [Key(27)]
    public int OpenIssuesDownloaded { get; init; }

    [Key(28)]
    public int BrainFilesCommitted { get; init; }

    [Key(29)]
    public bool BrainUpdatesPushed { get; init; }

    [Key(30)]
    public int DecompositionSubIssuesCreated { get; init; }

    [Key(31)]
    public int DecompositionSubIssuesAttempted { get; init; }

    [Key(32)]
    public PipelineStep? FinalStep { get; init; }

    [Key(33)]
    public string? FailureReason { get; init; }

    [Key(34)]
    public string? ModelName { get; init; }

    [Key(35)]
    public string? RepositoryName { get; init; }

    [Key(36)]
    public PipelineRunType RunType { get; init; }

    [Key(37)]
    public string? IssueIdentifier { get; init; }

    [Key(38)]
    public string? IssueTitle { get; init; }

    [Key(39)]
    public DateTimeOffset StartedAtOffset { get; init; }

    [Key(40)]
    public string? BrainProviderConfigId { get; init; }
}
