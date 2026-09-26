namespace CodingAgent.Pipeline.Models;

public sealed class PipelineRunSummary
{
    public required string RunId { get; init; }
    public required IssueIdentifier IssueIdentifier { get; init; }
    public required string IssueTitle { get; init; }

    /// <summary>Web URL of the issue on the provider, or null if unknown. Enables "open in provider" links.</summary>
    public string? IssueUrl { get; init; }

    /// <summary>Per-gate pass/fail outcomes from the run's final quality-gate report, or null if gates didn't run. Powers the Insights per-gate ranking.</summary>
    public IReadOnlyList<GateOutcome>? QualityGateOutcomes { get; init; }
    public required PipelineStep FinalStep { get; init; }

    /// <summary>
    /// The highest pipeline step reached before the run entered a terminal state.
    /// Populated from <see cref="PipelineRun.HighWaterMark"/> in <see cref="PipelineRun.ToSummary"/>.
    /// Null for runs persisted before this field was introduced — the UI renders no guessed failure
    /// step in that case. Stored in the JSONB SummaryJson column; no DB migration needed.
    /// </summary>
    public PipelineStep? LastActiveStep { get; init; }

    [Obsolete("Use StartedAtOffset for timezone-safe comparisons")]
    public DateTime StartedAt { get; init; }

    [Obsolete("Use CompletedAtOffset for timezone-safe comparisons")]
    public DateTime? CompletedAt { get; init; }

    /// <summary>Timezone-safe shadow of <see cref="StartedAt"/>. Set alongside the original property.</summary>
    public DateTimeOffset StartedAtOffset { get; init; }

    /// <summary>Timezone-safe shadow of <see cref="CompletedAt"/>. Set alongside the original property.</summary>
    public DateTimeOffset? CompletedAtOffset { get; init; }

    public int RetryCount { get; init; }
    public string? PullRequestUrl { get; init; }

    /// <summary>Discriminates implementation vs review runs.</summary>
    public PipelineRunType RunType { get; init; } = PipelineRunType.Implementation;

    /// <summary>PR URL for review runs (the PR being reviewed).</summary>
    public string? ReviewPrUrl { get; init; }

    /// <summary>Review agents that ran during this review run.</summary>
    public IReadOnlyList<string> CodeReviewAgentsRun { get; init; } = [];

    /// <summary>Critical finding count from code review.</summary>
    public int CodeReviewCriticalCount { get; init; }

    /// <summary>Warning finding count from code review.</summary>
    public int CodeReviewWarningCount { get; init; }

    /// <summary>Suggestion finding count from code review.</summary>
    public int CodeReviewSuggestionCount { get; init; }

    /// <summary>Model configured for the agent provider used in this run.</summary>
    public string? ModelName { get; init; }

    /// <summary>Whether a brain repository was used for this run.</summary>
    public bool BrainRepoUsed { get; init; }

    /// <summary>Whether brain updates were pushed successfully.</summary>
    public bool BrainUpdatesPushed { get; init; }

    /// <summary>Whether brain knowledge was loaded into this run's context.</summary>
    public bool BrainContextLoaded { get; init; }

    /// <summary>Number of brain knowledge files loaded into this run's context (0 when none/unused). Powers the Knowledge usage aggregate.</summary>
    public int BrainKnowledgeFileCount { get; init; }

    /// <summary>Which agent executed this run, or null for test-infrastructure runs.</summary>
    public string? AgentId { get; init; }

    /// <summary>How this run was initiated: "manual" or "loop".</summary>
    public string InitiatedBy { get; init; } = "manual";

    /// <summary>Analysis gate recommendation, or null if no assessment was produced.</summary>
    public AnalysisGateResult? AnalysisRecommendation { get; init; }

    /// <summary>What the pipeline did with the branch when this run started.</summary>
    public RunMode RunMode { get; init; } = RunMode.New;

    /// <summary>Why the run failed, or null if it did not fail.</summary>
    public string? FailureReason { get; init; }

    /// <summary>Structured feedback collected from the agent after this run.</summary>
    public RunFeedback? Feedback { get; init; }

    /// <summary>Accumulated total tokens across all agent invocations.</summary>
    public long TotalTokens { get; init; }

    /// <summary>Accumulated total cost (USD), or null if no cost data available.</summary>
    public decimal? TotalCost { get; init; }

    /// <summary>Accumulated tokens served from prompt cache across all agent invocations.</summary>
    public long CacheReadTokens { get; init; }

    /// <summary>Accumulated tokens written to prompt cache across all agent invocations.</summary>
    public long CacheWriteTokens { get; init; }

    /// <summary>Number of sub-issues successfully created during the Decomposition phase.</summary>
    public int DecompositionSubIssuesCreated { get; init; }

    /// <summary>Total number of sub-issues attempted during the Decomposition phase.</summary>
    public int DecompositionSubIssuesAttempted { get; init; }

    /// <summary>Project identifier for filtering/grouping. Null for legacy runs without project data.</summary>
    public string? ProjectId { get; init; }

    /// <summary>Project display name for run history table column.</summary>
    public string? ProjectName { get; init; }

    /// <summary>
    /// For decomposition runs: whether the epic was polled from the project-level
    /// EpicIssueProviderId ("project-level") or the template's own IssueProviderId ("template-level").
    /// Null for non-decomposition runs.
    /// </summary>
    public string? DecompositionSource { get; init; }

    /// <summary>Per-phase token/cost breakdown, or null if no phase data was collected (e.g. old runs).</summary>
    public IReadOnlyDictionary<string, PhaseUsage>? PhaseBreakdown { get; init; }

    /// <summary>Agent provider config ID for display name resolution in the UI.</summary>
    public string? AgentProviderConfigId { get; init; }

    /// <summary>
    /// Feature branch name for this run, or null if not yet set (e.g. run is still in analysis).
    /// Populated from <see cref="PipelineRun.BranchName"/> so the active-runs API endpoint
    /// can return it to the Scheduler for the housekeeping branch-update guard.
    /// </summary>
    public string? BranchName { get; init; }

    /// <summary>
    /// Git commit SHA of the agent container image that executed this run, or null for runs
    /// recorded before this field was introduced. Sourced from the SERVICE_VERSION environment
    /// variable injected at image build time via the BUILD_COMMIT_SHA Docker build arg.
    /// Use this to correlate outcome metrics back to a specific harness deployment.
    /// </summary>
    public string? HarnessVersion { get; init; }

    /// <summary>
    /// Last N lines of agent output captured at run completion.
    /// Null for runs completed before this field was introduced, or when the run produced no output.
    /// Stored in the JSONB SummaryJson column; no DB migration needed.
    /// N is governed by <see cref="PipelineConstants.OutputTailCapacity"/>.
    /// </summary>
    public IReadOnlyList<string>? OutputTail { get; init; }

    /// <summary>
    /// Issue provider config ID used for this run.
    /// Null for runs persisted before this field was introduced.
    /// Required to construct a re-dispatch <see cref="JobDistributionRequest"/> from the run page.
    /// Stored in the JSONB SummaryJson column; no DB migration needed.
    /// NOTE: The PipelineRunEntity also has an IssueProviderConfigId column, but that column is used
    /// as a consolidation-run sentinel (set to ConsolidationConstants.ProviderConfigId for consolidation
    /// runs, null otherwise). This summary field is independent and stores the actual provider config ID.
    /// </summary>
    public string? IssueProviderConfigId { get; init; }

    /// <summary>
    /// Repository provider config ID used for this run.
    /// Null for runs persisted before this field was introduced.
    /// Required to construct a re-dispatch <see cref="JobDistributionRequest"/> from the run page.
    /// Stored in the JSONB SummaryJson column; no DB migration needed.
    /// </summary>
    public string? RepoProviderConfigId { get; init; }

    /// <summary>
    /// Brain provider config ID used for this run, or null if no brain repo was configured.
    /// Null for runs persisted before this field was introduced.
    /// Stored in the JSONB SummaryJson column; no DB migration needed.
    /// </summary>
    public string? BrainProviderConfigId { get; init; }

    /// <summary>
    /// Pipeline (CI) provider config ID used for this run, or null if no pipeline provider was configured.
    /// Null for runs persisted before this field was introduced.
    /// Stored in the JSONB SummaryJson column; no DB migration needed.
    /// </summary>
    public string? PipelineProviderConfigId { get; init; }

    /// <summary>
    /// Consolidation run type (brain, refactoring, harness), or null for non-consolidation runs.
    /// Stored in the JSONB SummaryJson column; no DB migration needed.
    /// </summary>
    public ConsolidationRunType? ConsolidationType { get; init; }

    /// <summary>
    /// Consolidation template ID, or null for global scope (harness suggestions) or non-consolidation runs.
    /// Stored in the JSONB SummaryJson column; no DB migration needed.
    /// </summary>
    public string? ConsolidationTemplateId { get; init; }

    /// <summary>
    /// Human-readable result summary from the consolidation agent, or null if not yet available.
    /// Stored in the JSONB SummaryJson column; no DB migration needed.
    /// </summary>
    public string? ConsolidationResultSummary { get; init; }
}
