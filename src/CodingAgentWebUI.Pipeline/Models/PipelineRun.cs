using System.Collections.Concurrent;
using System.Threading;
using CodingAgentWebUI.Pipeline.Services;

namespace CodingAgentWebUI.Pipeline.Models;

public sealed class PipelineRun
{
    /// <summary>
    /// Creates a new <see cref="PipelineRun"/> with invariant defaults and all init-only properties.
    /// Mutable properties (RepositoryName, ModelName, ProjectId, etc.) should be set after construction.
    /// </summary>
    public static PipelineRun Create(
        string runId,
        string issueIdentifier,
        string issueTitle,
        string issueProviderConfigId,
        string repoProviderConfigId,
        PipelineRunType runType = PipelineRunType.Implementation,
        DateTimeOffset? startedAt = null,
        string initiatedBy = "manual",
        string? agentId = null,
        string? agentProviderConfigId = null,
        string? brainProviderConfigId = null,
        string? reviewPrBranchName = null,
        string? reviewPrTargetBranch = null,
        string? reviewPrUrl = null,
        string? reviewPrDescription = null,
        string? reviewPrAuthor = null,
        IReadOnlyList<LinkedIssueContext>? linkedIssueContexts = null,
        string? decompositionSource = null)
    {
        var now = startedAt ?? DateTimeOffset.UtcNow;
#pragma warning disable CS0618
        return new PipelineRun
        {
            RunId = runId,
            IssueIdentifier = issueIdentifier,
            IssueTitle = issueTitle,
            IssueProviderConfigId = issueProviderConfigId,
            RepoProviderConfigId = repoProviderConfigId,
            StartedAt = now.UtcDateTime,
            StartedAtOffset = now,
            // TODO: LastStepChangeAt is intentionally set independently from `now` — when startedAt is provided, these will differ (matches original AgentJobDispatcher behavior).
            LastStepChangeAt = DateTimeOffset.UtcNow,
            CurrentStep = PipelineStep.Created,
            InitiatedBy = initiatedBy,
            RunType = runType,
            AgentId = agentId,
            AgentProviderConfigId = agentProviderConfigId,
            BrainProviderConfigId = brainProviderConfigId,
            ReviewPrBranchName = reviewPrBranchName,
            ReviewPrTargetBranch = reviewPrTargetBranch,
            ReviewPrUrl = reviewPrUrl,
            ReviewPrDescription = reviewPrDescription,
            ReviewPrAuthor = reviewPrAuthor,
            LinkedIssueContexts = linkedIssueContexts,
            DecompositionSource = decompositionSource
        };
#pragma warning restore CS0618
    }

    public required string RunId { get; init; }
    public required string IssueIdentifier { get; init; }
    // NOTE: Semantically set-once (populated after construction from fetched issue title). Cannot be init-only without restructuring call sites.
    public required string IssueTitle { get; set; }
    public required string IssueProviderConfigId { get; init; }
    public required string RepoProviderConfigId { get; init; }

    /// <summary>
    /// Current pipeline step. Uses a volatile backing field to ensure cross-thread visibility
    /// since this is read by HeartbeatMonitorService and written by SignalR hub methods.
    /// </summary>
    public PipelineStep CurrentStep
    {
        get => (PipelineStep)Volatile.Read(ref _currentStep);
        set => Volatile.Write(ref _currentStep, (int)value);
    }
    private int _currentStep;

    /// <summary>Highest pipeline step ever reached during this run (excludes terminal states). Used by the sidebar to show revisited steps.</summary>
    public PipelineStep HighWaterMark { get; set; }

    [Obsolete("Use StartedAtOffset for timezone-safe comparisons")]
    public DateTime StartedAt { get; init; }

    [Obsolete("Use CompletedAtOffset for timezone-safe comparisons")]
    public DateTime? CompletedAt { get; set; }

    /// <summary>Timezone-safe shadow of <see cref="StartedAt"/>. Set alongside the original property.</summary>
    public DateTimeOffset StartedAtOffset { get; init; }

    /// <summary>Timezone-safe shadow of <see cref="CompletedAt"/>. Set alongside the original property.</summary>
    public DateTimeOffset? CompletedAtOffset { get; set; }

    /// <summary>Atomically sets both <see cref="CompletedAt"/> and <see cref="CompletedAtOffset"/> to the current UTC time.</summary>
    public void MarkCompleted()
    {
        var now = DateTimeOffset.UtcNow;
#pragma warning disable CS0618
        CompletedAt = now.UtcDateTime;
#pragma warning restore CS0618
        CompletedAtOffset = now;
    }

    /// <summary>Atomically sets both <see cref="CompletedAt"/> and <see cref="CompletedAtOffset"/> from the provided timestamp.</summary>
    public void MarkCompleted(DateTimeOffset timestamp)
    {
#pragma warning disable CS0618
        CompletedAt = timestamp.UtcDateTime;
#pragma warning restore CS0618
        CompletedAtOffset = timestamp;
    }

    /// <summary>
    /// Last time the pipeline step changed (set via ReportStepTransition).
    /// Used by HeartbeatMonitorService to detect stuck-in-Busy agents.
    /// Uses Interlocked on ticks for thread-safe reads/writes (DateTimeOffset is not atomic).
    /// </summary>
    public DateTimeOffset LastStepChangeAt
    {
        get => new DateTimeOffset(Interlocked.Read(ref _lastStepChangeAtTicks), TimeSpan.Zero);
        set => Interlocked.Exchange(ref _lastStepChangeAtTicks, value.UtcTicks);
    }
    private long _lastStepChangeAtTicks;

    public string? WorkspacePath { get; set; }
    public string? BranchName { get; set; }
    public string? FailureReason { get; set; }
    public string? PullRequestUrl { get; set; }

    /// <summary>The PR body content as last set. Used by the description generation step to prepend agent summary.</summary>
    public string? PullRequestBody { get; set; }

    /// <summary>Cohesive sub-state group for execution metrics (MAINT-13). Delegating properties below preserve the existing API surface.</summary>
    public RunMetrics Metrics { get; } = new();

    public int RetryCount { get => Metrics.RetryCount; set => Metrics.RetryCount = value; }

    /// <summary>Number of infrastructure CI retries (does not consume agent retry budget).</summary>
    public int InfrastructureRetryCount { get => Metrics.InfrastructureRetryCount; set => Metrics.InfrastructureRetryCount = value; }

    /// <summary>Agent-generated analysis content, populated during AnalyzingCode step.</summary>
    public string? AnalysisContent { get; set; }

    /// <summary>Number of code review iterations completed during the ReviewingCode step.</summary>
    public int CodeReviewIterationsCompleted { get; set; }

    /// <summary>Current code review iteration in progress (1-based), 0 when not reviewing.</summary>
    public int CodeReviewIterationInProgress { get; set; }

    /// <summary>Total code review iterations configured for this run.</summary>
    public int CodeReviewIterationsTotal { get; set; }

    private int _codeReviewCriticalCount;
    private int _codeReviewWarningCount;
    private int _codeReviewSuggestionCount;

    /// <summary>Number of [CRITICAL] findings detected across all review iterations. Thread-safe read via Volatile.Read.</summary>
    public int CodeReviewCriticalCount => Volatile.Read(ref _codeReviewCriticalCount);

    /// <summary>Number of [WARNING] findings detected across all review iterations. Thread-safe read via Volatile.Read.</summary>
    public int CodeReviewWarningCount => Volatile.Read(ref _codeReviewWarningCount);

    /// <summary>Number of [SUGGESTION] findings detected across all review iterations. Thread-safe read via Volatile.Read.</summary>
    public int CodeReviewSuggestionCount => Volatile.Read(ref _codeReviewSuggestionCount);

    /// <summary>Atomically adds to the code review severity counters. Use when accumulating counts from concurrent review agents.</summary>
    public void AddCodeReviewCounts(int critical, int warning, int suggestion)
    {
        Interlocked.Add(ref _codeReviewCriticalCount, critical);
        Interlocked.Add(ref _codeReviewWarningCount, warning);
        Interlocked.Add(ref _codeReviewSuggestionCount, suggestion);
    }

    /// <summary>Atomically replaces the code review severity counters. Use when setting absolute values from a completion payload.</summary>
    public void SetCodeReviewCounts(int critical, int warning, int suggestion)
    {
        Interlocked.Exchange(ref _codeReviewCriticalCount, critical);
        Interlocked.Exchange(ref _codeReviewWarningCount, warning);
        Interlocked.Exchange(ref _codeReviewSuggestionCount, suggestion);
    }

    /// <summary>Number of inline comments successfully submitted in this review.</summary>
    public int InlineCommentsPosted { get; set; }

    /// <summary>Whether fallback to body-only occurred (retries exhausted, API rejection, etc.).</summary>
    public bool InlineCommentsDegraded { get; set; }

    /// <summary>Reason for degradation, or null if inline comments posted successfully.</summary>
    public string? InlineCommentsDegradedReason { get; set; }

    /// <summary>Per-agent findings accumulated across all review iterations.</summary>
    public ConcurrentDictionary<string, string> CodeReviewAgentFindings { get; } = new();

    /// <summary>Thread-safe collections — mutated by orchestration service while UI reads via OnChange.</summary>
    public BoundedConcurrentQueue<string> RetryErrors { get; init; } = new(PipelineConstants.DefaultRetryErrorsCapacity);
    public BoundedConcurrentQueue<ChatEntry> ChatHistory { get; init; } = new(PipelineConstants.DefaultChatHistoryCapacity);
    public QualityGateReport? LatestQualityReport { get; set; }
    public BoundedConcurrentQueue<string> OutputLines { get; init; } = new(PipelineConstants.DefaultOutputLinesCapacity);

    /// <summary>Issue labels, populated when the issue is fetched.</summary>
    public IReadOnlyList<string> IssueLabels { get; set; } = Array.Empty<string>();

    /// <summary>History of quality gate reports across retry attempts.</summary>
    public BoundedConcurrentQueue<QualityGateReport> QualityGateHistory { get; init; } = new(PipelineConstants.DefaultQualityGateHistoryCapacity);

    /// <summary>Whether the baseline health check passed (null if step was skipped/disabled).</summary>
    public bool? BaselineHealthPassed { get; set; }

    /// <summary>Whether existing analysis was reused (skipped agent analysis).</summary>
    public bool AnalysisSkipped { get; set; }

    /// <summary>Analysis gate recommendation, or null if no assessment was produced.</summary>
    public AnalysisGateResult? AnalysisRecommendation { get; set; }

    /// <summary>Structured acceptance criteria compliance report, or null if not produced/disabled.</summary>
    public AcceptanceCriteriaReport? AcceptanceCriteriaReport { get; set; }

    /// <summary>The authoritative label the agent applied for this run's terminal state (e.g. "agent:needs-refinement", "agent:wont-do"). Null if not explicitly set.</summary>
    public string? FinalLabel { get; set; }

    /// <summary>Non-blocking concerns from the analysis assessment.</summary>
    public IReadOnlyList<string> AnalysisConcerns { get; set; } = Array.Empty<string>();

    /// <summary>Hard blockers from the analysis assessment.</summary>
    public IReadOnlyList<string> AnalysisBlockingIssues { get; set; } = Array.Empty<string>();

    /// <summary>Whether the PR is a draft (quality gates failed after max retries).</summary>
    public bool IsDraftPr { get; set; }

    /// <summary>Repository display name (owner/repo).</summary>
    public string? RepositoryName { get; set; }

    /// <summary>Session ID captured from the code generation agent, used for --resume-id in fix prompts.</summary>
    public string? CodegenSessionId { get; set; }

    /// <summary>Number of files changed during code generation, updated after agent execution.</summary>
    public int FilesChangedCount { get => Metrics.FilesChangedCount; set => Metrics.FilesChangedCount = value; }

    /// <summary>Lines added during code generation.</summary>
    public int LinesAdded { get => Metrics.LinesAdded; set => Metrics.LinesAdded = value; }

    /// <summary>Lines removed during code generation.</summary>
    public int LinesRemoved { get => Metrics.LinesRemoved; set => Metrics.LinesRemoved = value; }

    /// <summary>PR number extracted from the PR URL (e.g. "47").</summary>
    public string? PullRequestNumber { get; set; }

    /// <summary>Files excluded from the commit due to blacklist rules (from GIT-04).</summary>
    // NOTE: [ARC-10] Setter allows mutable List<string> assignment behind IReadOnlyList interface — deferred to separate issue
    public IReadOnlyList<string> BlacklistedFilesDetected { get; set; } = Array.Empty<string>();

    /// <summary>Model configured for the agent provider used in this run (e.g. "auto", "claude-sonnet-4.6").</summary>
    public string? ModelName { get; set; }

    /// <summary>Names of review agents that were executed during this run.</summary>
    public IReadOnlyList<string> CodeReviewAgentsRun { get; set; } = Array.Empty<string>();

    /// <summary>Brain repository provider config ID, or null if no brain repo selected.</summary>
    public string? BrainProviderConfigId { get; init; }

    /// <summary>Pipeline provider config ID, or null if no pipeline provider configured.</summary>
    public string? PipelineProviderConfigId { get; set; }

    /// <summary>Whether brain context was successfully loaded during pre-run sync.</summary>
    public bool BrainContextLoaded { get; set; }

    /// <summary>Number of knowledge files available in the .brain/ directory after sync.</summary>
    public int BrainKnowledgeFileCount { get; set; }

    /// <summary>Whether post-run brain updates were pushed successfully.</summary>
    public bool BrainUpdatesPushed { get; set; }

    /// <summary>Number of brain files committed during post-run sync.</summary>
    public int BrainFilesCommitted { get; set; }

    /// <summary>Brain update validation result from BrainUpdateService.</summary>
    public BrainValidationResult? BrainValidation { get; set; }

    /// <summary>Linked agent PR detected during rework mode, or null for new-issue runs.</summary>
    public LinkedPullRequest? LinkedPullRequest { get; set; }

    /// <summary>Files with conflicts from rebase onto base branch, empty if no conflicts.</summary>
    public IReadOnlyList<string> MergeConflictFiles { get; set; } = Array.Empty<string>();

    /// <summary>Whether merge conflicts were force-resolved using incoming (main wins).</summary>
    public bool MergeForceResolved { get; set; }

    /// <summary>How this run was initiated: "manual" or "loop".</summary>
    public string InitiatedBy { get; init; } = "manual";

    /// <summary>Discriminates implementation vs review runs.</summary>
    public PipelineRunType RunType { get; init; } = PipelineRunType.Implementation;

    /// <summary>PR branch name for review runs.</summary>
    public string? ReviewPrBranchName { get; init; }

    /// <summary>PR target branch for review runs (e.g., "main", "develop"). Used for diff computation.</summary>
    public string? ReviewPrTargetBranch { get; init; }

    /// <summary>PR URL for review runs.</summary>
    public string? ReviewPrUrl { get; init; }

    /// <summary>PR body/description for review runs.</summary>
    public string? ReviewPrDescription { get; init; }

    /// <summary>PR author username for review runs (used for [HUMAN/AUTHOR] attribution).</summary>
    public string? ReviewPrAuthor { get; init; }

    /// <summary>Pre-fetched linked issue details for review runs.</summary>
    public IReadOnlyList<LinkedIssueContext>? LinkedIssueContexts { get; init; }

    /// <summary>
    /// Which agent is executing this run, or null for test runs.
    /// Uses <see cref="Volatile"/> read/write to ensure cross-thread visibility since this is
    /// written by dispatch paths (SignalRWorkDistributor, PendingWorkItemDrainService,
    /// RunLifecycleManager.AgentAcceptedRunAsync) and read by HeartbeatMonitorService.
    /// </summary>
    public string? AgentId
    {
        get => Volatile.Read(ref _agentId);
        set => Volatile.Write(ref _agentId, value);
    }
    private string? _agentId;

    /// <summary>Agent provider config ID used for this run, or null for test runs.</summary>
    public string? AgentProviderConfigId { get; init; }

    /// <summary>Project ID that owned the dispatching template at dispatch time.</summary>
    public string? ProjectId { get; set; }

    /// <summary>Project display name for UI rendering without reverse-lookup.</summary>
    public string? ProjectName { get; set; }

    /// <summary>
    /// For decomposition runs: whether the epic was polled from the project-level
    /// EpicIssueProviderId ("project-level") or the template's own IssueProviderId ("template-level").
    /// Null for non-decomposition runs.
    /// </summary>
    public string? DecompositionSource { get; init; }

    /// <summary>Agent Profile Id that was resolved at dispatch time.</summary>
    public string? ResolvedProfileId { get; set; }

    /// <summary>Ids of all Quality Gate Configuration entities resolved for this job.</summary>
    public IReadOnlyList<string> ResolvedQualityGateConfigIds { get; set; } = Array.Empty<string>();

    /// <summary>Ids of all Reviewer Configuration entities resolved for this job.</summary>
    public IReadOnlyList<string> ResolvedReviewerConfigIds { get; set; } = Array.Empty<string>();

    /// <summary>Structured feedback collected from the agent after this run completes.</summary>
    public RunFeedback? Feedback { get; set; }

    /// <summary>Accumulated total tokens across all agent invocations in this run.</summary>
    public long TotalTokens { get => Metrics.TotalTokens; set => Metrics.TotalTokens = value; }

    /// <summary>Accumulated total cost (USD, decimal) across all agent invocations, or null if no cost data available.</summary>
    public decimal? TotalCost { get => Metrics.TotalCost; set => Metrics.TotalCost = value; }

    /// <summary>Number of sub-issues successfully created during the Decomposition phase.</summary>
    public int DecompositionSubIssuesCreated { get; set; }

    /// <summary>Total number of sub-issues attempted during the Decomposition phase.</summary>
    public int DecompositionSubIssuesAttempted { get; set; }

    /// <summary>Results of individual sub-issue creation attempts during the Decomposition phase.</summary>
    public IReadOnlyList<SubIssueCreationResult> SubIssueResults { get; set; } = [];

    /// <summary>Number of open issues downloaded for deduplication context during decomposition.</summary>
    public int OpenIssuesDownloaded { get; set; }

    /// <summary>Creates a <see cref="PipelineRunSummary"/> from this run's current state.</summary>
    // NOTE: [ARC-10] FinalStep = CurrentStep without terminal state guard — edge case if called before TransitionTo completes
    #pragma warning disable CS0618 // Obsolete members used intentionally for backward-compat serialization
    public PipelineRunSummary ToSummary() => new()
    {
        RunId = RunId,
        IssueIdentifier = IssueIdentifier,
        IssueTitle = IssueTitle,
        FinalStep = CurrentStep,
        StartedAt = StartedAt,
        CompletedAt = CompletedAt,
        StartedAtOffset = StartedAtOffset,
        CompletedAtOffset = CompletedAtOffset,
        RetryCount = RetryCount,
        PullRequestUrl = PullRequestUrl,
        RunType = RunType,
        ReviewPrUrl = ReviewPrUrl,
        CodeReviewAgentsRun = CodeReviewAgentsRun,
        CodeReviewCriticalCount = CodeReviewCriticalCount,
        CodeReviewWarningCount = CodeReviewWarningCount,
        CodeReviewSuggestionCount = CodeReviewSuggestionCount,
        ModelName = ModelName,
        BrainRepoUsed = BrainProviderConfigId != null,
        BrainUpdatesPushed = BrainUpdatesPushed,
        AgentId = AgentId,
        InitiatedBy = InitiatedBy,
        AnalysisRecommendation = AnalysisRecommendation,
        IsRework = LinkedPullRequest != null,
        FailureReason = FailureReason,
        Feedback = Feedback,
        TotalTokens = TotalTokens,
        TotalCost = TotalCost,
        PhaseBreakdown = Metrics.PhaseBreakdown.Count > 0
            ? Metrics.PhaseBreakdown.ToDictionary(kvp => kvp.Key, kvp => kvp.Value)
            : null,
        DecompositionSubIssuesCreated = DecompositionSubIssuesCreated,
        DecompositionSubIssuesAttempted = DecompositionSubIssuesAttempted,
        ProjectName = ProjectName,
        DecompositionSource = DecompositionSource
    };
    #pragma warning restore CS0618
}
