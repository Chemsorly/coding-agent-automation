using MessagePack;
// Aliased: the DataAnnotations namespace also has a KeyAttribute, which would clash with MessagePack's [Key].
using RangeAttribute = System.ComponentModel.DataAnnotations.RangeAttribute;
using static CodingAgent.Pipeline.Models.PipelineConfigurationDefaults;

namespace CodingAgent.Pipeline.Models;

/// <summary>
/// The global pipeline settings. Every property is read by code, and every one that is not internal has a field
/// on a settings page and a row in docs/configuration.md; tests enforce both.
/// <para>
/// Limits are <see cref="RangeAttribute"/>s on the properties, checked by <see cref="PipelineSettingsValidator"/> when
/// settings are saved or imported. Setters never throw, so a stored value outside its range still loads.
/// </para>
/// <para>
/// Internal properties (no settings field): <see cref="ClosedLoopAutoStart"/> is loop state,
/// <see cref="PipelineInjectedPaths"/> is filled in at runtime, <see cref="TransientRetryDelay"/> lets tests skip the wait,
/// and <see cref="WorkspaceBaseDirectory"/> is part of the agent image layout.
/// </para>
/// </summary>
[MessagePackObject]
public sealed record PipelineConfiguration
{
    // ── Retry & Timeout settings ────────────────────────────────────────

    [Key(41)]
    [ProjectOverridable(Order = 1)]
    [Range(0, 10)]
    public int MaxRetries { get; init; } = 3;

    /// <summary>
    /// Maximum number of retry attempts for the analysis phase.
    /// Default 2 = 3 total attempts (initial + 2 retries).
    /// Set to 0 to disable retry (fail on first failure).
    /// </summary>
    [Key(35)]
    [ProjectOverridable(Order = 2)]
    [Range(0, 10)]
    public int MaxAnalysisRetries { get; init; } = 2;

    /// <summary>
    /// Limit for each agent call, in every run type, and the job deadline: Kubernetes stops the job after
    /// this value plus 60 seconds.
    /// </summary>
    [Key(4)]
    [ProjectOverridable(Order = 3)]
    [Range(typeof(TimeSpan), "00:01:00", "1.00:00:00")]
    public TimeSpan AgentTimeout
    {
        get => _agentTimeout;
        init
        {
            // A stored zero (legal before validation existed, or a null that TimeSpanJsonConverter reads as zero)
            // becomes the default, so old rows keep working without a migration.
            _agentTimeout = value == TimeSpan.Zero ? PipelineConstants.DefaultAgentTimeout : value;
        }
    }
    private readonly TimeSpan _agentTimeout = PipelineConstants.DefaultAgentTimeout;

    /// <summary>
    /// How long the agent can be silent (no output) before the stall monitor logs a warning.
    /// The warning resets after each occurrence so it fires again after another interval of silence.
    /// </summary>
    [Key(51)]
    [ProjectOverridable(Order = 17)]
    [Range(typeof(TimeSpan), "00:00:30", "01:00:00")]
    public TimeSpan StallWarningInterval { get; init; } = PipelineConstants.DefaultStallWarningInterval;

    /// <summary>
    /// How often the stall monitor polls <see cref="IAgentProvider.GetHealthStatus"/>.
    /// Default is 30 seconds. Tests can set a shorter interval for faster execution.
    /// </summary>
    [Key(50)]
    [Range(typeof(TimeSpan), "00:00:05", "00:02:00")]
    public TimeSpan StallPollInterval { get; init; } = PipelineConstants.DefaultStallPollInterval;

    // ── Workspace settings ──────────────────────────────────────────────

    /// <summary>
    /// Internal: the directory agents create run workspaces in, <c>{WorkspaceBaseDirectory}/{runId}</c>. The agent images
    /// provide <c>/app/workspaces</c>, which the default resolves to, so this is not an operator setting.
    /// </summary>
    [Key(52)]
    public string WorkspaceBaseDirectory { get; init; } = "./workspaces";

    // Key(27) retired — FailedWorkspaceRetentionDays removed (workspaces live in agent pods and go with them). Do NOT reuse this Key index

    // ── External CI settings ────────────────────────────────────────────

    [Key(26)]
    [ProjectOverridable(Order = 12)]
    [Range(typeof(TimeSpan), "00:01:00", "1.00:00:00")]
    public TimeSpan ExternalCiTimeout { get; init; } = PipelineConstants.DefaultExternalCiTimeout;

    [Key(25)]
    [ProjectOverridable(Order = 13)]
    [Range(typeof(TimeSpan), "00:00:05", "00:05:00")]
    public TimeSpan ExternalCiPollInterval { get; init; } = PipelineConstants.DefaultExternalCiPollInterval;

    /// <summary>
    /// How long to wait for CI runs to appear before concluding CI never started.
    /// Triggers a re-push retry instead of burning the full ExternalCiTimeout. Default: 10 minutes.
    /// </summary>
    [Key(53)]
    [ProjectOverridable(Order = 14)]
    [Range(typeof(TimeSpan), "00:01:00", "00:30:00")]
    public TimeSpan CiNotStartedTimeout { get; init; } = PipelineConstants.DefaultCiNotStartedTimeout;

    /// <summary>
    /// Maximum re-push retries when CI never starts. Default: 15.
    /// </summary>
    [Key(54)]
    [ProjectOverridable(Order = 15)]
    [Range(0, 20)]
    public int CiNotStartedMaxRetries { get; init; } = PipelineConstants.DefaultCiNotStartedMaxRetries;

    [Key(38)]
    [ProjectOverridable(Order = 16)]
    [Range(0, 10)]
    public int MaxInfrastructureRetries { get; init; } = 5;

    // ── Closed-loop settings ────────────────────────────────────────────

    /// <summary>
    /// Internal: whether the pipeline loop runs, and so starts on application startup.
    /// Set to true when user starts the loop, false when user stops it.
    /// </summary>
    [Key(15)]
    public bool ClosedLoopAutoStart { get; init; }

    /// <summary>
    /// Poll interval for the closed pipeline loop when checking for new agent:next issues.
    /// Default: 60 seconds.
    /// </summary>
    [Key(21)]
    [Range(typeof(TimeSpan), "00:00:10", "00:10:00")]
    public TimeSpan ClosedLoopPollInterval { get; init; } = PipelineConstants.DefaultClosedLoopPollInterval;

    /// <summary>
    /// Maximum number of issues to process per poll cycle in the closed loop.
    /// 0 means unlimited (process entire backlog). Counter resets each poll cycle.
    /// </summary>
    [Key(20)]
    [Range(0, 1000)]
    public int ClosedLoopMaxRunsPerCycle { get; init; } = 0;

    /// <summary>
    /// Number of consecutive poll failures before the circuit breaker pauses the loop.
    /// Default: 5.
    /// </summary>
    [Key(18)]
    [Range(1, 50)]
    public int ClosedLoopMaxConsecutivePollFailures { get; init; } = 5;

    // Key(17) retired — ClosedLoopMaxBackoffInterval removed (the loop pauses through its circuit breaker, it has no backoff). Do NOT reuse this Key index

    /// <summary>
    /// Maximum number of pages to fetch when polling for agent:next issues.
    /// Each page contains up to 100 issues. Default: 10 (1000 issues max).
    /// </summary>
    [Key(19)]
    [Range(1, 100)]
    public int ClosedLoopMaxPagesToFetch { get; init; } = 10;

    /// <summary>
    /// Cooldown duration before the circuit breaker auto-resumes polling.
    /// After this period the loop resets failure counters and retries. Default: 5 minutes.
    /// </summary>
    [Key(16)]
    [Range(typeof(TimeSpan), "00:00:30", "01:00:00")]
    public TimeSpan ClosedLoopCircuitBreakerCooldown { get; init; } = PipelineConstants.DefaultClosedLoopCircuitBreakerCooldown;

    // ── Agent orchestration settings ────────────────────────────────────

    /// <summary>
    /// Global fallback for agent label routing when a repository's ProviderConfig
    /// does not specify <see cref="ProviderConfig.RequiredLabels"/>. Comma-separated string (e.g., "kiro,dotnet").
    /// Null means any idle agent can be selected.
    /// </summary>
    [Key(24)]
    public string? DefaultRequiredAgentLabels { get; init; }

    /// <summary>
    /// Maximum number of retry attempts when brain repo push fails with a non-fast-forward error
    /// (concurrent push conflict). Each retry fetches, rebases, resolves conflicts, and retries push.
    /// Default: 3.
    /// </summary>
    [Key(12)]
    [Range(0, 10)]
    public int BrainPushMaxRetries { get; init; } = 3;

    /// <summary>
    /// When true, the brain repository operates in read-only mode: pre-run sync
    /// (clone/pull) and context injection proceed normally, but all write operations
    /// are skipped — write instructions are omitted from the prompt, validation is
    /// skipped, and the SyncingBrainRepoPostRun step (commit and push) is skipped
    /// entirely. Brain consolidation does not run either. Defaults to false.
    /// </summary>
    [Key(13)]
    [ProjectOverridable(Order = 27)]
    public bool BrainReadOnly { get; init; }

    // Key(3) retired — AgentDisconnectGracePeriod removed (nothing read it). Do NOT reuse this Key index
    // Key(2) retired — AgentBusyProgressTimeout removed (nothing read it). Do NOT reuse this Key index
    // Key(42) retired — OutputBufferCapacity removed (the buffer size is PipelineConstants.DefaultOutputBufferCapacity). Do NOT reuse this Key index
    // Key(43) retired — OutputLinesCapacity removed (PipelineRun uses PipelineConstants.DefaultOutputLinesCapacity). Do NOT reuse this Key index
    // Key(14) retired — ChatHistoryCapacity removed (PipelineRun uses PipelineConstants.DefaultChatHistoryCapacity). Do NOT reuse this Key index
    // Key(46) retired — QualityGateHistoryCapacity removed (PipelineRun uses PipelineConstants.DefaultQualityGateHistoryCapacity). Do NOT reuse this Key index
    // Key(49) retired — RetryErrorsCapacity removed (PipelineRun uses PipelineConstants.DefaultRetryErrorsCapacity). Do NOT reuse this Key index
    // Key(29) retired — HeartbeatSweepIntervalSeconds removed (its heartbeat monitor was deleted). Do NOT reuse this Key index
    // Key(30) retired — HeartbeatTimeoutSeconds removed (its heartbeat monitor was deleted). Do NOT reuse this Key index

    /// <summary>
    /// Interval in minutes between orphaned label recovery sweeps.
    /// Default: 30.
    /// </summary>
    [Key(55)]
    [Range(5, 1440)]
    public int OrphanedLabelSweepIntervalMinutes { get; init; } = PipelineConstants.DefaultOrphanedLabelSweepIntervalMinutes;

    // ── Commit settings ─────────────────────────────────────────────────

    [Key(10)]
    [ProjectOverridable(Order = 26)]
    public IReadOnlyList<string> BlacklistedPaths { get; init; } = new[] { AgentWorkspacePaths.MetadataDirectory, AgentWorkspacePaths.BrainDirectory };

    /// <summary>
    /// Internal: agent-provider-specific paths that are ALWAYS unstaged before commit, regardless of
    /// <see cref="BlacklistedPaths"/> configuration. Populated from
    /// <see cref="IAgentProvider.PipelineInjectedPaths"/> at pipeline startup.
    /// </summary>
    [Key(44)]
    public IReadOnlyList<string> PipelineInjectedPaths { get; init; } = Array.Empty<string>();

    // ── Analysis & Review settings ──────────────────────────────────────

    // Key(33) retired — IssuePageSize removed (nothing read it). Do NOT reuse this Key index

    [Key(22)]
    [ProjectOverridable(Order = 10, DeepMerge = true)]
    public CodeReviewConfiguration CodeReview { get; init; } = new();

    [Key(5)]
    [ProjectOverridable(Order = 4)]
    public string AnalysisPrompt { get; init; } = DefaultAnalysisPrompt;

    [Key(32)]
    [ProjectOverridable(Order = 5)]
    public string ImplementationPrompt { get; init; } = DefaultImplementationPrompt;

    /// <summary>
    /// When true, a second agent reviews the analysis in an isolated session and feeds
    /// findings back to the original analysis agent for refinement. This adversarial
    /// loop improves analysis quality by catching missed components, incorrect assumptions,
    /// and feasibility issues before implementation begins. Default: true.
    /// </summary>
    [Key(7)]
    [ProjectOverridable(Order = 6)]
    public bool AnalysisReviewEnabled { get; init; } = true;

    /// <summary>
    /// Prompt sent to the isolated review agent that evaluates the analysis.
    /// The agent reads .agent/analysis.md, .agent/analysis-assessment.json, and .agent/issue-context.md,
    /// then writes findings to .agent/analysis-review.md.
    /// </summary>
    [Key(8)]
    [ProjectOverridable(Order = 7)]
    public string AnalysisReviewPrompt { get; init; } = DefaultAnalysisReviewPrompt;

    /// <summary>
    /// Prompt sent back to the original analysis session instructing it to refine
    /// the analysis based on the review feedback at .agent/analysis-review.md.
    /// </summary>
    [Key(6)]
    [ProjectOverridable(Order = 8)]
    public string AnalysisRefinementPrompt { get; init; } = DefaultAnalysisRefinementPrompt;

    /// <summary>
    /// When true, a dedicated acceptance criteria compliance check runs in parallel with
    /// code reviewers, producing a structured JSON report. Default: true.
    /// </summary>
    [Key(0)]
    [ProjectOverridable(Order = 9)]
    public bool AcceptanceCriteriaEnabled { get; init; } = true;

    /// <summary>
    /// Prompt sent to the acceptance criteria agent that evaluates implementation compliance.
    /// The agent writes structured JSON to .agent/acceptance-criteria.json.
    /// </summary>
    [Key(1)]
    public string AcceptanceCriteriaPrompt { get; init; } = DefaultPrompts.AcceptanceCriteriaCompliance;

    /// <summary>
    /// When true, refactoring proposals are reviewed by an isolated discriminator agent
    /// before issues are created. Default: true.
    /// </summary>
    [Key(48)]
    [ProjectOverridable(Order = 23)]
    public bool RefactoringReviewEnabled { get; init; } = true;

    /// <summary>
    /// When true, brain consolidation changes are reviewed by an isolated discriminator
    /// agent before being committed. Default: true.
    /// </summary>
    [Key(11)]
    [ProjectOverridable(Order = 24)]
    public bool BrainConsolidationReviewEnabled { get; init; } = true;

    /// <summary>
    /// When true, harness suggestions are reviewed by an isolated discriminator agent
    /// before being persisted. Default: true. Harness suggestions are global, so projects cannot override this.
    /// </summary>
    [Key(28)]
    public bool HarnessSuggestionsReviewEnabled { get; init; } = true;

    /// <summary>
    /// When true, the pipeline runs a baseline health check (agent environment + workspace build)
    /// after branch creation and before code analysis. Default: true.
    /// </summary>
    [Key(9)]
    [ProjectOverridable(Order = 11)]
    public bool BaselineHealthCheckEnabled { get; init; } = true;

    /// <summary>
    /// Number of commits on the default branch since the last analysis that triggers
    /// an automatic analysis refresh. Set to 0 to disable commit-count staleness detection.
    /// Valid range: 0–1000 (at PageSize=100, 1000 commits = max 10 API calls).
    /// </summary>
    [Key(56)]
    [ProjectOverridable(Order = 28)]
    [Range(0, 1000)]
    public int AnalysisCommitThreshold { get; init; } = PipelineConstants.DefaultAnalysisCommitThreshold;

    // Key(34) retired — LastUsedProviderIds removed (nothing read it). Do NOT reuse this Key index

    // ── Multi-repo pipeline loop ────────────────────────────────────────

    // Key(45): retired (was PipelineJobTemplates) — do NOT reuse this Key index

    /// <summary>
    /// Maximum number of refactoring proposals the agent is instructed to produce
    /// and the executor will create issues for. Controls both the prompt instruction
    /// ("Produce at most N proposals") and the issue creation cap in RefactoringExecutor.
    /// Default: 3.
    /// </summary>
    [Key(40)]
    [ProjectOverridable(Order = 22)]
    [Range(1, 10)]
    public int MaxRefactoringProposals { get; init; } = 3;

    /// <summary>
    /// Time window for git hotspot analysis in refactoring detection.
    /// Only commits within this window are counted. Default: 90 days.
    /// </summary>
    [Key(31)]
    [Range(typeof(TimeSpan), "7.00:00:00", "365.00:00:00")]
    public TimeSpan HotspotAnalysisLookback { get; init; } = TimeSpan.FromDays(90);

    /// <summary>
    /// Maximum number of sub-issues per epic decomposition (range: 1–20). Default: 10.
    /// </summary>
    [Key(37)]
    [ProjectOverridable(Order = 18)]
    [Range(1, 20)]
    public int MaxDecompositionSubIssues { get; init; } = 10;

    /// <summary>
    /// Maximum simultaneous decomposition runs across all projects. Default: 2.
    /// The scheduler enforces it for the whole loop, so projects cannot override it.
    /// </summary>
    [Key(36)]
    [Range(1, 10)]
    public int MaxConcurrentDecompositions { get; init; } = 2;

    // Key(23) retired — DecompositionTimeout removed; decomposition calls use AgentTimeout. Do NOT reuse this Key index

    /// <summary>
    /// Maximum open issues downloaded for deduplication context. Default: 50.
    /// </summary>
    [Key(39)]
    [ProjectOverridable(Order = 21)]
    [Range(1, 200)]
    public int MaxOpenIssuesForContext { get; init; } = 50;

    /// <summary>
    /// Time window for querying past refactoring proposal outcomes.
    /// Only closed issues within this window are included in the feedback context.
    /// Default: 90 days.
    /// </summary>
    [Key(47)]
    [Range(typeof(TimeSpan), "7.00:00:00", "365.00:00:00")]
    public TimeSpan RefactoringOutcomeLookback { get; init; } = TimeSpan.FromDays(90);

    // ── Issue image extraction settings ─────────────────────────────────

    /// <summary>
    /// Maximum number of images to extract per issue/PR. Default: 10.
    /// </summary>
    [Key(63)]
    [Range(0, 50)]
    public int MaxIssueImages { get; init; } = 10;

    /// <summary>
    /// Maximum size in bytes for a single downloaded image. Default: 5 MB. Range: 1–50 MB.
    /// </summary>
    [Key(64)]
    [Range(1_048_576d, 52_428_800d)]
    public long MaxImageSizeBytes { get; init; } = 5_242_880;

    /// <summary>
    /// Maximum total bytes for all downloaded images combined. Default: 20 MB. Range: 1–200 MB.
    /// </summary>
    [Key(65)]
    [Range(1_048_576d, 209_715_200d)]
    public long MaxTotalImageSizeBytes { get; init; } = 20_971_520;

    /// <summary>
    /// Total time budget in seconds for downloading all images. Default: 60.
    /// </summary>
    [Key(66)]
    [Range(5, 600)]
    public int TotalImageDownloadTimeoutSeconds { get; init; } = 60;

    /// <summary>
    /// When true, issue/PR image extraction and download is enabled. Default: true.
    /// </summary>
    [Key(67)]
    public bool EnableIssueImageExtraction { get; init; } = true;

    /// <summary>
    /// When true, downloaded images are sent as native image parts to the agent API. Default: true.
    /// </summary>
    [Key(68)]
    public bool EnableNativeImageParts { get; init; } = true;

    // Key(69) retired — ImageDownloadTimeoutSeconds removed (only the total download budget applies). Do NOT reuse this Key index

    // ── Decomposition file limit settings ───────────────────────────────

    /// <summary>
    /// Maximum files a single decomposition sub-issue may create or modify (range: 1–30). Default: 12.
    /// </summary>
    [Key(70)]
    [ProjectOverridable(Order = 29)]
    [Range(1, 30)]
    public int MaxDecompositionSubIssueFiles { get; init; } = 12;

    // ── Kubernetes model-fetch settings ─────────────────────────────────

    /// <summary>
    /// Timeout in seconds for the model-fetch k8s Job (caa-models-*).
    /// Increase on slow setups where image pull or pod scheduling takes longer than the default.
    /// Default: 120s. Range: 30–600.
    /// </summary>
    [Key(71)]
    [Range(30, 600)]
    public int ModelFetchTimeoutSeconds { get; init; } = 120;

    // ── Housekeeping settings ─────────────────────────────────────────────

    /// <summary>
    /// Maximum number of PRs allowed to be simultaneously in the "update triggered,
    /// CI running" state per repository. Tracked across poll ticks via an in-flight set.
    /// Default: 1 (fully serial). Minimum: 1.
    /// </summary>
    [Key(72)]
    [Range(1, 20)]
    public int HousekeepingConcurrencyLimit { get; init; } = 1;

    /// <summary>
    /// How often (in minutes) the stale branch cleanup pass runs. The cleanup lists all
    /// agent branches and deletes those with no open PR and an inactive issue label.
    /// Default: 60 minutes. Set to 0 to run every poll tick (not recommended for busy repos).
    /// </summary>
    [Key(73)]
    [Range(0, 10080)]
    public int HousekeepingBranchCleanupIntervalMinutes { get; init; } = 60;

    /// <summary>
    /// Minimum time in minutes between consecutive branch-update triggers for the same PR.
    /// Prevents a single PR from monopolising the update slot when CI takes longer than one
    /// poll cycle. Default: 25 (comfortably exceeds a typical ~20-min CI run).
    /// </summary>
    [Key(82)]
    [Range(1, 1440)]
    public int HousekeepingTriggerCooldownMinutes { get; init; } = 25;

    // Key(84) retired — HousekeepingMaxSlotAgeMinutes removed

    // Key(74) retired — MaxConsolidationDispatchRetries removed (its drain service was removed in #2323). Do NOT reuse this Key index

    // ── DB retention settings ─────────────────────────────────────────────

    /// <summary>
    /// Per-project row count cap for <c>PipelineRuns</c>. Only completed runs
    /// (<c>CompletedAt IS NOT NULL</c>, <c>ProjectId IS NOT NULL</c>) are eligible for deletion.
    /// The oldest rows beyond N per project are pruned on each sweep. 0 or -1 (the default) keeps every row.
    /// </summary>
    [Key(75)]
    [Range(-1, 1_000_000)]
    public int PipelineRunRetentionCount { get; init; } = -1;

    /// <summary>
    /// Per-project row count cap for terminal <c>WorkItems</c>
    /// (<c>Status IN (3=Succeeded, 4=Failed, 5=Cancelled)</c>, <c>CompletedAt IS NOT NULL</c>,
    /// <c>ProjectId IS NOT NULL</c>). Non-terminal rows and rows with <c>CompletedAt IS NULL</c>
    /// are never deleted. 0 or -1 (the default) keeps every row.
    /// </summary>
    [Key(76)]
    [Range(-1, 1_000_000)]
    public int WorkItemRetentionCount { get; init; } = -1;

    // Key(77) retired — DbRetentionSweepInterval removed (the Scheduler triggers the sweep hourly). Do NOT reuse this Key index

    /// <summary>
    /// Internal: delay between retry loop iterations when a transient provider error
    /// (ProviderRateLimit or ProviderOverload) is encountered. Default: 30 seconds.
    /// Tests can set this to <see cref="TimeSpan.Zero"/> to avoid blocking.
    /// </summary>
    [Key(78)]
    public TimeSpan TransientRetryDelay { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// When true, the closed-loop cycle runs a sweep after each dispatch pass to cancel Pending
    /// WorkItems whose issue or PR is no longer in the current cycle's eligibility set.
    /// <list type="bullet">
    ///   <item><see cref="WorkItemTaskType.Implementation"/> — compared against the issue eligibility map.</item>
    ///   <item><see cref="WorkItemTaskType.Review"/> — compared against the PR eligibility map.</item>
    ///   <item><see cref="WorkItemTaskType.Decomposition"/> and <see cref="WorkItemTaskType.Consolidation"/> — skipped (fail-open).</item>
    /// </list>
    /// Default: true (opt-out).
    /// </summary>
    [Key(79)]
    public bool QueueSweepEnabled { get; init; } = true;

    /// <summary>
    /// Maximum number of re-poll attempts when CI is cancelled because the branch HEAD moved
    /// to a new commit (e.g. a teammate's push, a bot merge-from-main, or the pipeline's own
    /// retry commit triggering GitHub's cancel-in-progress concurrency rule).
    /// On each attempt the pipeline reads the current HEAD SHA; if it differs from the polled SHA,
    /// it re-enters <see cref="PollCiWithNotStartedRetryAsync"/> on the new HEAD instead of treating
    /// the cancellation as a gate failure and consuming an outer retry slot.
    /// Default: 3. Valid range: 0–10.
    /// </summary>
    // Note: PollAndHandleInfraRetryAsync enforces the ExternalCiTimeout budget across the initial
    // poll and all branch-moved re-polls using a single linked CancellationTokenSource, so total
    // CI wait time is bounded by one ExternalCiTimeout window.
    [Key(80)]
    [ProjectOverridable(Order = 31)]
    [Range(0, 10)]
    public int CiCancelledMoveMaxRetries { get; init; } = PipelineConstants.DefaultCiCancelledMoveMaxRetries;

    /// <summary>
    /// Timeout in seconds for the agent call during feedback collection (both success-path
    /// <see cref="PullRequestFinalizationService.CollectFeedbackAsync"/> and failure-path
    /// <c>CollectFailureFeedbackAsync</c> in <c>QualityGateExecutor</c>).
    /// Default: 60 — matches the previous hard-coded <see cref="FeedbackConstraints.FailureFeedbackTimeoutSeconds"/>.
    /// </summary>
    [Key(81)]
    [ProjectOverridable(Order = 32)]
    [Range(10, 600)]
    public int FeedbackTimeoutSeconds { get; init; } = FeedbackConstraints.FailureFeedbackTimeoutSeconds;

    /// <summary>
    /// Minimum number of Implementation issue slots reserved per dispatch cycle.
    /// When PRs (or other higher-priority work) would otherwise consume all slots,
    /// this many slots are held back for Issues if any are present and
    /// <see cref="DispatchRoundRobinRequest.MaxRunsPerCycle"/> ≥ 2 (or is 0 for unlimited).
    /// Default: 1. Set to 0 to disable floor allocation (strict priority, original behavior).
    /// The scheduler enforces it for the whole loop, so projects cannot override it.
    /// </summary>
    [Key(83)]
    [Range(0, 100)]
    public int MinIssueSlots { get; init; } = 1;

    // ── Feedback comment outbox ────────────────────────────────────────────
    /// <summary>
    /// Maximum number of delivery attempts for a durable feedback comment before it is
    /// permanently marked Failed and excluded from the relay sweep.
    /// Default 5. See FeedbackCommentRelayService.
    /// </summary>
    [Key(85)]
    [Range(1, 100)]
    public int FeedbackCommentOutboxMaxAttempts { get; init; } = PipelineConstants.DefaultFeedbackCommentOutboxMaxAttempts;

    // Keys 86–88 are left for the automatic consolidation settings planned in #3591.

    // ── Triage ─────────────────────────────────────────────────────────────
    /// <summary>
    /// When true, a triage's root cause analysis and drafts are reviewed by an isolated discriminator
    /// agent, and refined when it finds problems, before the result is reported. Default: true.
    /// </summary>
    [Key(89)]
    [ProjectOverridable(Order = 33)]
    public bool TriageReviewEnabled { get; init; } = true;

    /// <summary>
    /// Days a triage is kept after its last change (attempt, edit, issue creation). Created issues and
    /// tracker comments are not affected. The retention sweep runs for the whole system, so projects
    /// cannot override it. Default: 30. Valid range: 1–365.
    /// </summary>
    [Key(90)]
    [Range(1, 365)]
    public int TriageRetentionDays { get; init; } = PipelineConstants.DefaultTriageRetentionDays;
}
