using CodingAgentWebUI.Pipeline.Services;
using MessagePack;
using static CodingAgentWebUI.Pipeline.Models.PipelineConfigurationDefaults;

namespace CodingAgentWebUI.Pipeline.Models;

[MessagePackObject]
public sealed record PipelineConfiguration
{
    // ── Retry & Timeout settings ────────────────────────────────────────

    [Key(41)]
    [ProjectOverridable(Order = 1)]
    public int MaxRetries { get; init; } = 3;

    /// <summary>
    /// Maximum number of retry attempts for the analysis phase.
    /// Default 1 = 2 total attempts (initial + 1 retry).
    /// Set to 0 to disable retry (fail on first failure).
    /// </summary>
    [Key(35)]
    [ProjectOverridable(Order = 2)]
    public int MaxAnalysisRetries { get; init; } = 2;

    [Key(4)]
    [ProjectOverridable(Order = 3)]
    public TimeSpan AgentTimeout { get; init; } = PipelineConstants.DefaultAgentTimeout;

    /// <summary>
    /// How long the agent can be silent (no output) before the stall monitor logs a warning.
    /// The warning resets after each occurrence so it fires again after another interval of silence.
    /// </summary>
    [Key(51)]
    [ProjectOverridable(Order = 17)]
    public TimeSpan StallWarningInterval { get; init; } = PipelineConstants.DefaultStallWarningInterval;

    /// <summary>
    /// How often the stall monitor polls <see cref="IAgentProvider.GetHealthStatus"/>.
    /// Default is 30 seconds. Tests can set a shorter interval for faster execution.
    /// </summary>
    [Key(50)]
    public TimeSpan StallPollInterval { get; init; } = PipelineConstants.DefaultStallPollInterval;

    // ── Workspace settings ──────────────────────────────────────────────

    [Key(52)]
    public string WorkspaceBaseDirectory { get; init; } = "./workspaces";

    /// <summary>
    /// Number of days to retain workspace folders for failed or cancelled runs.
    /// Set to 0 to delete immediately. Set to -1 to retain indefinitely.
    /// </summary>
    [Key(27)]
    public int FailedWorkspaceRetentionDays { get; init; } = 7;

    // ── External CI settings ────────────────────────────────────────────

    [Key(26)]
    [ProjectOverridable(Order = 12)]
    public TimeSpan ExternalCiTimeout { get; init; } = PipelineConstants.DefaultExternalCiTimeout;

    [Key(25)]
    [ProjectOverridable(Order = 13)]
    public TimeSpan ExternalCiPollInterval { get; init; } = PipelineConstants.DefaultExternalCiPollInterval;

    /// <summary>
    /// How long to wait for CI runs to appear before concluding CI never started.
    /// Triggers a re-push retry instead of burning the full ExternalCiTimeout. Default: 5 minutes.
    /// </summary>
    [Key(53)]
    [ProjectOverridable(Order = 14)]
    public TimeSpan CiNotStartedTimeout { get; init; } = PipelineConstants.DefaultCiNotStartedTimeout;

    /// <summary>
    /// Maximum re-push retries when CI never starts. Default: 5.
    /// </summary>
    [Key(54)]
    [ProjectOverridable(Order = 15)]
    public int CiNotStartedMaxRetries
    {
        get => _ciNotStartedMaxRetries;
        init => _ciNotStartedMaxRetries = value is >= 0 and <= 20
            ? value
            : throw new ArgumentOutOfRangeException(nameof(CiNotStartedMaxRetries), value, "Value must be between 0 and 20.");
    }
    private readonly int _ciNotStartedMaxRetries = PipelineConstants.DefaultCiNotStartedMaxRetries;

    [Key(38)]
    [ProjectOverridable(Order = 16)]
    public int MaxInfrastructureRetries
    {
        get => _maxInfrastructureRetries;
        init => _maxInfrastructureRetries = value is >= 0 and <= 10
            ? value
            : throw new ArgumentOutOfRangeException(nameof(MaxInfrastructureRetries), value, "Value must be between 0 and 10.");
    }
    private readonly int _maxInfrastructureRetries = 5;

    // ── Closed-loop settings ────────────────────────────────────────────

    /// <summary>
    /// When true, the pipeline loop starts automatically on application startup.
    /// Set to true when user starts the loop, false when user stops it.
    /// </summary>
    [Key(15)]
    public bool ClosedLoopAutoStart { get; init; }

    /// <summary>
    /// Poll interval for the closed pipeline loop when checking for new agent:next issues.
    /// Default: 60 seconds.
    /// </summary>
    [Key(21)]
    public TimeSpan ClosedLoopPollInterval { get; init; } = PipelineConstants.DefaultClosedLoopPollInterval;

    /// <summary>
    /// Maximum number of issues to process per poll cycle in the closed loop.
    /// 0 means unlimited (process entire backlog). Counter resets each poll cycle.
    /// </summary>
    [Key(20)]
    public int ClosedLoopMaxRunsPerCycle { get; init; } = 0;

    /// <summary>
    /// Number of consecutive poll failures before the circuit breaker pauses the loop.
    /// Default: 5.
    /// </summary>
    [Key(18)]
    public int ClosedLoopMaxConsecutivePollFailures
    {
        get => _closedLoopMaxConsecutivePollFailures;
        init => _closedLoopMaxConsecutivePollFailures = value >= 1
            ? value
            : throw new ArgumentOutOfRangeException(nameof(ClosedLoopMaxConsecutivePollFailures), value, "Value must be at least 1.");
    }
    private readonly int _closedLoopMaxConsecutivePollFailures = 5;

    /// <summary>
    /// Maximum backoff interval between poll retries after consecutive failures.
    /// Backoff uses exponential formula capped at this value. Default: 15 minutes.
    /// </summary>
    [Key(17)]
    public TimeSpan ClosedLoopMaxBackoffInterval { get; init; } = PipelineConstants.DefaultClosedLoopMaxBackoffInterval;

    /// <summary>
    /// Maximum number of pages to fetch when polling for agent:next issues.
    /// Each page contains up to 100 issues. Default: 10 (1000 issues max).
    /// </summary>
    [Key(19)]
    public int ClosedLoopMaxPagesToFetch
    {
        get => _closedLoopMaxPagesToFetch;
        init => _closedLoopMaxPagesToFetch = value >= 1
            ? value
            : throw new ArgumentOutOfRangeException(nameof(ClosedLoopMaxPagesToFetch), value, "Value must be at least 1.");
    }
    private readonly int _closedLoopMaxPagesToFetch = 10;

    /// <summary>
    /// Cooldown duration before the circuit breaker auto-resumes polling.
    /// After this period the loop resets failure counters and retries. Default: 5 minutes.
    /// </summary>
    [Key(16)]
    public TimeSpan ClosedLoopCircuitBreakerCooldown
    {
        get => _closedLoopCircuitBreakerCooldown;
        init => _closedLoopCircuitBreakerCooldown = value >= TimeSpan.FromSeconds(1)
            ? value
            : throw new ArgumentOutOfRangeException(nameof(ClosedLoopCircuitBreakerCooldown), value, "Value must be at least 1 second.");
    }
    private readonly TimeSpan _closedLoopCircuitBreakerCooldown = PipelineConstants.DefaultClosedLoopCircuitBreakerCooldown;

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
    public int BrainPushMaxRetries { get; init; } = 3;

    /// <summary>
    /// When true, the brain repository operates in read-only mode: pre-run sync
    /// (clone/pull) and context injection proceed normally, but all write operations
    /// are skipped — write instructions are omitted from the prompt, validation is
    /// skipped, and the SyncingBrainRepoPostRun step (commit and push) is skipped
    /// entirely. Defaults to false.
    /// </summary>
    [Key(13)]
    [ProjectOverridable(Order = 27)]
    public bool BrainReadOnly { get; init; }

    /// <summary>
    /// How long to wait after an agent disconnects before marking its active run as Failed.
    /// Default: 5 minutes.
    /// </summary>
    [Key(3)]
    public TimeSpan AgentDisconnectGracePeriod { get; init; } = PipelineConstants.DefaultAgentDisconnectGracePeriod;

    /// <summary>
    /// How long a busy agent can go without pipeline step progress before being marked as stuck.
    /// Default: 60 minutes.
    /// </summary>
    [Key(2)]
    public TimeSpan AgentBusyProgressTimeout { get; init; } = PipelineConstants.DefaultAgentBusyProgressTimeout;

    /// <summary>
    /// Maximum number of output lines to retain per active pipeline run (ring buffer capacity).
    /// Default: 10,000.
    /// </summary>
    [Key(42)]
    public int OutputBufferCapacity { get; init; } = PipelineConstants.DefaultOutputBufferCapacity;

    /// <summary>
    /// Maximum number of output lines in PipelineRun.OutputLines bounded queue.
    /// Default: 5,000.
    /// </summary>
    [Key(43)]
    public int OutputLinesCapacity { get; init; } = PipelineConstants.DefaultOutputLinesCapacity;

    /// <summary>
    /// Maximum number of chat entries in PipelineRun.ChatHistory bounded queue.
    /// Default: 200.
    /// </summary>
    [Key(14)]
    public int ChatHistoryCapacity { get; init; } = PipelineConstants.DefaultChatHistoryCapacity;

    /// <summary>
    /// Maximum number of quality gate reports in PipelineRun.QualityGateHistory bounded queue.
    /// Default: 50.
    /// </summary>
    [Key(46)]
    public int QualityGateHistoryCapacity { get; init; } = PipelineConstants.DefaultQualityGateHistoryCapacity;

    /// <summary>
    /// Maximum number of retry error messages in PipelineRun.RetryErrors bounded queue.
    /// Default: 100.
    /// </summary>
    [Key(49)]
    public int RetryErrorsCapacity { get; init; } = PipelineConstants.DefaultRetryErrorsCapacity;

    /// <summary>
    /// Interval in seconds between heartbeat monitor sweeps. Requires restart to take effect.
    /// Default: 60.
    /// </summary>
    [Key(29)]
    public int HeartbeatSweepIntervalSeconds { get; init; } = PipelineConstants.DefaultHeartbeatSweepIntervalSeconds;

    /// <summary>
    /// Seconds without a heartbeat before an agent is considered stale.
    /// Default: 90.
    /// </summary>
    [Key(30)]
    public int HeartbeatTimeoutSeconds { get; init; } = PipelineConstants.DefaultHeartbeatTimeoutSeconds;

    /// <summary>
    /// Interval in minutes between orphaned label recovery sweeps.
    /// Default: 30.
    /// </summary>
    [Key(55)]
    public int OrphanedLabelSweepIntervalMinutes { get; init; } = PipelineConstants.DefaultOrphanedLabelSweepIntervalMinutes;

    // ── Commit settings ─────────────────────────────────────────────────

    [Key(10)]
    [ProjectOverridable(Order = 26)]
    public IReadOnlyList<string> BlacklistedPaths { get; init; } = new[] { ".agent", ".brain" };

    /// <summary>
    /// Agent-provider-specific paths that are ALWAYS unstaged before commit, regardless of
    /// <see cref="BlacklistedPaths"/> configuration. Populated from
    /// <see cref="IAgentProvider.PipelineInjectedPaths"/> at pipeline startup.
    /// </summary>
    [Key(44)]
    public IReadOnlyList<string> PipelineInjectedPaths { get; init; } = Array.Empty<string>();

    // ── Analysis & Review settings ──────────────────────────────────────

    [Key(33)]
    public int IssuePageSize { get; init; } = 25;

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
    /// before being persisted. Default: true.
    /// </summary>
    [Key(28)]
    [ProjectOverridable(Order = 25)]
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
    /// Configurable at global level and overridable per project via <see cref="PipelineConfigurationResolver.ApplyProjectOverrides"/>.
    /// </summary>
    [Key(56)]
    [ProjectOverridable(Order = 28)]
    public int AnalysisCommitThreshold { get; init; } = PipelineConstants.DefaultAnalysisCommitThreshold;

    /// <summary>
    /// Records the last-used provider ID for each provider selection per pipeline.
    /// Keys: "issue", "repository", "agent", "brain", "pipeline".
    /// Values: provider config IDs.
    /// Pre-populates dropdowns on subsequent pipeline runs.
    /// </summary>
    [Key(34)]
    public IReadOnlyDictionary<string, string> LastUsedProviderIds { get; init; } = new Dictionary<string, string>();

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
    public int MaxRefactoringProposals { get; init; } = 3;

    /// <summary>
    /// Time window for git hotspot analysis in refactoring detection.
    /// Only commits within this window are counted. Default: 90 days.
    /// </summary>
    [Key(31)]
    public TimeSpan HotspotAnalysisLookback { get; init; } = TimeSpan.FromDays(90);

    /// <summary>
    /// Maximum number of sub-issues per epic decomposition (range: 1–20). Default: 10.
    /// </summary>
    [Key(37)]
    [ProjectOverridable(Order = 18)]
    public int MaxDecompositionSubIssues
    {
        get => field;
        init
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(value, 1, nameof(MaxDecompositionSubIssues));
            ArgumentOutOfRangeException.ThrowIfGreaterThan(value, 20, nameof(MaxDecompositionSubIssues));
            field = value;
        }
    } = 10;

    /// <summary>
    /// Maximum simultaneous decomposition runs. Default: 2.
    /// </summary>
    [Key(36)]
    [ProjectOverridable(Order = 19)]
    public int MaxConcurrentDecompositions { get; init; } = 2;

    /// <summary>
    /// Timeout for each decomposition phase. Default: 15 minutes.
    /// </summary>
    [Key(23)]
    [ProjectOverridable(Order = 20)]
    public TimeSpan DecompositionTimeout { get; init; } = TimeSpan.FromMinutes(15);

    /// <summary>
    /// Maximum open issues downloaded for deduplication context. Default: 50.
    /// </summary>
    [Key(39)]
    [ProjectOverridable(Order = 21)]
    public int MaxOpenIssuesForContext { get; init; } = 50;

    /// <summary>
    /// Time window for querying past refactoring proposal outcomes.
    /// Only closed issues within this window are included in the feedback context.
    /// Default: 90 days.
    /// </summary>
    [Key(47)]
    public TimeSpan RefactoringOutcomeLookback { get; init; } = TimeSpan.FromDays(90);

    // ── Issue image extraction settings ─────────────────────────────────

    /// <summary>
    /// Maximum number of images to extract per issue/PR. Default: 10.
    /// </summary>
    [Key(63)]
    public int MaxIssueImages { get; init; } = 10;

    /// <summary>
    /// Maximum size in bytes for a single downloaded image. Default: 5 MB.
    /// </summary>
    [Key(64)]
    public long MaxImageSizeBytes { get; init; } = 5_242_880;

    /// <summary>
    /// Maximum total bytes for all downloaded images combined. Default: 20 MB.
    /// </summary>
    [Key(65)]
    public long MaxTotalImageSizeBytes { get; init; } = 20_971_520;

    /// <summary>
    /// Total time budget in seconds for downloading all images. Default: 60.
    /// </summary>
    [Key(66)]
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

    /// <summary>
    /// Timeout in seconds for downloading a single image. Default: 30.
    /// </summary>
    [Key(69)]
    public int ImageDownloadTimeoutSeconds { get; init; } = 30;

}
