using System.Text.Json.Serialization;

namespace CodingAgentWebUI.Pipeline.Models;

/// <summary>
/// Controls how blacklisted path violations are handled during commits.
/// </summary>
public enum BlacklistMode
{
    /// <summary>Unstage blacklisted files, log a warning, and continue the pipeline.</summary>
    WarnAndExclude,

    /// <summary>Fail the pipeline with a clear error listing the violating files.</summary>
    Fail
}

/// <summary>
/// Configuration for a single specialized review agent.
/// </summary>
public sealed record ReviewAgentConfig
{
    public required string Name { get; init; }
    public required string Prompt { get; init; }
}

/// <summary>
/// Controls whether review agents share the codegen session or run in isolation.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ReviewIsolation
{
    /// <summary>Review agents share the codegen session (legacy behavior).</summary>
    Shared,

    /// <summary>Review agents run in fresh sessions with no shared context.</summary>
    Isolated
}

public sealed record CodeReviewConfiguration
{
    public bool Enabled { get; init; } = true;
    public int MaxIterations { get; init; } = 2;
    public string Prompt { get; init; } = PipelineConfiguration.DefaultCodeReviewPrompt;

    /// <summary>
    /// When set, the review step splits into find-then-fix: the review prompt reports findings
    /// with severity markers, then this fix prompt is sent only if [CRITICAL] findings exist.
    /// When null/empty, falls back to single-pass behavior (review prompt does both find and fix).
    /// </summary>
    public string? FixPrompt { get; init; }

    /// <summary>
    /// Controls whether review agents share the codegen session or run in fresh isolated sessions.
    /// Default is Isolated to eliminate self-attribution bias.
    /// </summary>
    public ReviewIsolation ReviewIsolation { get; init; } = ReviewIsolation.Isolated;
}

public sealed record PipelineConfiguration
{
    public const string DefaultCodeReviewPrompt = DefaultPrompts.CodeReview;
    public const string DefaultFixPrompt = DefaultPrompts.Fix;
    public const string DefaultCorrectnessReviewPrompt = DefaultPrompts.CorrectnessReview;
    public const string DefaultDotNetSpecialistReviewPrompt = DefaultPrompts.DotNetSpecialistReview;
    public const string DefaultSecurityReviewPrompt = DefaultPrompts.SecurityReview;
    public const string DefaultAcceptanceCriteriaReviewPrompt = DefaultPrompts.AcceptanceCriteriaReview;

    /// <summary>Default review agents: Correctness + DotNetSpecialist + Security + AcceptanceCriteria.</summary>
    public static IReadOnlyList<ReviewAgentConfig> DefaultReviewAgents { get; } = new[]
    {
        new ReviewAgentConfig { Name = "Correctness", Prompt = DefaultCorrectnessReviewPrompt },
        new ReviewAgentConfig { Name = "DotNetSpecialist", Prompt = DefaultDotNetSpecialistReviewPrompt },
        new ReviewAgentConfig { Name = "SecurityReviewer", Prompt = DefaultSecurityReviewPrompt },
        new ReviewAgentConfig { Name = "AcceptanceCriteria", Prompt = DefaultAcceptanceCriteriaReviewPrompt }
    };

    public const string DefaultAnalysisPrompt = DefaultPrompts.Analysis;
    public const string DefaultImplementationPrompt = DefaultPrompts.Implementation;

    // ── Domain-specific sub-configurations (source of truth) ────────────

    /// <summary>Retry and timeout settings.</summary>
    [JsonIgnore]
    public RetryConfiguration Retry { get; init; } = new();

    /// <summary>Workspace directory and retention settings.</summary>
    [JsonIgnore]
    public WorkspaceConfiguration Workspace { get; init; } = new();

    /// <summary>External CI integration settings.</summary>
    [JsonIgnore]
    public ExternalCiConfiguration ExternalCi { get; init; } = new();

    /// <summary>Closed-loop polling settings.</summary>
    [JsonIgnore]
    public ClosedLoopConfiguration ClosedLoop { get; init; } = new();

    /// <summary>Multi-agent orchestration settings.</summary>
    [JsonIgnore]
    public AgentConfiguration Agent { get; init; } = new();

    /// <summary>Commit blacklist and enforcement settings.</summary>
    [JsonIgnore]
    public CommitConfiguration Commit { get; init; } = new();

    // ── Flat properties (JSON serialization surface, delegate to sub-configs) ──

    public int MaxRetries
    {
        get => Retry.MaxRetries;
        init => Retry = Retry with { MaxRetries = value };
    }

    /// <summary>
    /// Maximum number of retry attempts for the analysis phase.
    /// Default 1 = 2 total attempts (initial + 1 retry).
    /// Set to 0 to disable retry (fail on first failure).
    /// </summary>
    public int MaxAnalysisRetries
    {
        get => Retry.MaxAnalysisRetries;
        init => Retry = Retry with { MaxAnalysisRetries = value };
    }

    public int IssuePageSize { get; init; } = 25;

    public TimeSpan AgentTimeout
    {
        get => Retry.AgentTimeout;
        init => Retry = Retry with { AgentTimeout = value };
    }

    public string WorkspaceBaseDirectory
    {
        get => Workspace.WorkspaceBaseDirectory;
        init => Workspace = Workspace with { WorkspaceBaseDirectory = value };
    }

    public CodeReviewConfiguration CodeReview { get; init; } = new();
    public string AnalysisPrompt { get; init; } = DefaultAnalysisPrompt;
    public string ImplementationPrompt { get; init; } = DefaultImplementationPrompt;

    /// <summary>
    /// When true, the pipeline runs a baseline health check (agent environment + workspace build)
    /// after branch creation and before code analysis. Default: true.
    /// </summary>
    public bool BaselineHealthCheckEnabled { get; init; } = true;

    public bool ExternalCiEnabled
    {
        get => ExternalCi.ExternalCiEnabled;
        init => ExternalCi = ExternalCi with { ExternalCiEnabled = value };
    }

    public TimeSpan ExternalCiTimeout
    {
        get => ExternalCi.ExternalCiTimeout;
        init => ExternalCi = ExternalCi with { ExternalCiTimeout = value };
    }

    public TimeSpan ExternalCiPollInterval
    {
        get => ExternalCi.ExternalCiPollInterval;
        init => ExternalCi = ExternalCi with { ExternalCiPollInterval = value };
    }

    /// <summary>
    /// How long the agent can be silent (no output) before the stall monitor logs a warning.
    /// The warning resets after each occurrence so it fires again after another interval of silence.
    /// </summary>
    public TimeSpan StallWarningInterval
    {
        get => Retry.StallWarningInterval;
        init => Retry = Retry with { StallWarningInterval = value };
    }

    /// <summary>
    /// How often the stall monitor polls <see cref="IAgentProvider.GetHealthStatus"/>.
    /// Default is 30 seconds. Tests can set a shorter interval for faster execution.
    /// </summary>
    public TimeSpan StallPollInterval
    {
        get => Retry.StallPollInterval;
        init => Retry = Retry with { StallPollInterval = value };
    }

    public IReadOnlyList<string> BlacklistedPaths
    {
        get => Commit.BlacklistedPaths;
        init => Commit = Commit with { BlacklistedPaths = value };
    }

    public BlacklistMode BlacklistMode
    {
        get => Commit.BlacklistMode;
        init => Commit = Commit with { BlacklistMode = value };
    }

    /// <summary>
    /// Number of days to retain workspace folders for failed or cancelled runs.
    /// Set to 0 to delete immediately. Set to -1 to retain indefinitely.
    /// </summary>
    public int FailedWorkspaceRetentionDays
    {
        get => Workspace.FailedWorkspaceRetentionDays;
        init => Workspace = Workspace with { FailedWorkspaceRetentionDays = value };
    }

    /// <summary>
    /// Records the last-used provider ID for each provider selection per pipeline.
    /// Keys: "issue", "repository", "agent", "brain", "pipeline".
    /// Values: provider config IDs.
    /// Pre-populates dropdowns on subsequent pipeline runs.
    /// </summary>
    public IReadOnlyDictionary<string, string> LastUsedProviderIds { get; init; } = new Dictionary<string, string>();

    /// <summary>
    /// When true, the brain repository operates in read-only mode: pre-run sync
    /// (clone/pull) and context injection proceed normally, but all write operations
    /// are skipped — write instructions are omitted from the prompt, validation is
    /// skipped, and the SyncingBrainRepoPostRun step (commit and push) is skipped
    /// entirely. Defaults to false.
    /// </summary>
    public bool BrainReadOnly
    {
        get => Agent.BrainReadOnly;
        init => Agent = Agent with { BrainReadOnly = value };
    }

    /// <summary>
    /// Poll interval for the closed pipeline loop when checking for new agent:next issues.
    /// Default: 60 seconds.
    /// </summary>
    public TimeSpan ClosedLoopPollInterval
    {
        get => ClosedLoop.ClosedLoopPollInterval;
        init => ClosedLoop = ClosedLoop with { ClosedLoopPollInterval = value };
    }

    /// <summary>
    /// Maximum number of issues to process per poll cycle in the closed loop.
    /// 0 means unlimited (process entire backlog). Counter resets each poll cycle.
    /// </summary>
    public int ClosedLoopMaxRunsPerCycle
    {
        get => ClosedLoop.ClosedLoopMaxRunsPerCycle;
        init => ClosedLoop = ClosedLoop with { ClosedLoopMaxRunsPerCycle = value };
    }

    /// <summary>
    /// Number of consecutive poll failures before the circuit breaker pauses the loop.
    /// Default: 5.
    /// </summary>
    public int ClosedLoopMaxConsecutivePollFailures
    {
        get => ClosedLoop.ClosedLoopMaxConsecutivePollFailures;
        init => ClosedLoop = ClosedLoop with { ClosedLoopMaxConsecutivePollFailures = value };
    }

    /// <summary>
    /// Maximum backoff interval between poll retries after consecutive failures.
    /// Backoff uses exponential formula capped at this value. Default: 15 minutes.
    /// </summary>
    public TimeSpan ClosedLoopMaxBackoffInterval
    {
        get => ClosedLoop.ClosedLoopMaxBackoffInterval;
        init => ClosedLoop = ClosedLoop with { ClosedLoopMaxBackoffInterval = value };
    }

    /// <summary>
    /// Maximum number of pages to fetch when polling for agent:next issues.
    /// Each page contains up to 100 issues. Default: 10 (1000 issues max).
    /// </summary>
    public int ClosedLoopMaxPagesToFetch
    {
        get => ClosedLoop.ClosedLoopMaxPagesToFetch;
        init => ClosedLoop = ClosedLoop with { ClosedLoopMaxPagesToFetch = value };
    }

    // ── Multi-agent fields ──────────────────────────────────────────────

    /// <summary>
    /// Global fallback for agent label routing when a repository's ProviderConfig
    /// does not specify <c>requiredAgentLabels</c>. Comma-separated string (e.g., "kiro,dotnet").
    /// Null means any idle agent can be selected.
    /// </summary>
    public string? DefaultRequiredAgentLabels
    {
        get => Agent.DefaultRequiredAgentLabels;
        init => Agent = Agent with { DefaultRequiredAgentLabels = value };
    }

    /// <summary>
    /// Maximum number of retry attempts when brain repo push fails with a non-fast-forward error
    /// (concurrent push conflict). Each retry fetches, rebases, resolves conflicts, and retries push.
    /// Default: 3.
    /// </summary>
    public int BrainPushMaxRetries
    {
        get => Agent.BrainPushMaxRetries;
        init => Agent = Agent with { BrainPushMaxRetries = value };
    }

    /// <summary>
    /// How long to wait after an agent disconnects before marking its active run as Failed.
    /// Default: 5 minutes.
    /// </summary>
    public TimeSpan AgentDisconnectGracePeriod
    {
        get => Agent.AgentDisconnectGracePeriod;
        init => Agent = Agent with { AgentDisconnectGracePeriod = value };
    }

    /// <summary>
    /// Maximum number of output lines to retain per active pipeline run (ring buffer capacity).
    /// Default: 10,000.
    /// </summary>
    public int OutputBufferCapacity
    {
        get => Agent.OutputBufferCapacity;
        init => Agent = Agent with { OutputBufferCapacity = value };
    }

    // ── Multi-repo pipeline loop ────────────────────────────────────────

    /// <summary>
    /// List of pipeline job templates for multi-repo round-robin polling.
    /// Each template pairs an issue provider with a repository provider.
    /// When non-empty, the pipeline loop iterates through enabled templates each cycle.
    /// </summary>
    public IReadOnlyList<PipelineJobTemplate> PipelineJobTemplates { get; init; } = Array.Empty<PipelineJobTemplate>();
}
