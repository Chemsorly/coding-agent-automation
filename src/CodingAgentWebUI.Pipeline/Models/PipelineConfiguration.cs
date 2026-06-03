using System.Text.Json.Serialization;
using Serilog;

namespace CodingAgentWebUI.Pipeline.Models;

public sealed record PipelineConfiguration
{
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
    public const string DefaultAnalysisReviewPrompt = DefaultPrompts.AnalysisReview;
    public const string DefaultAnalysisRefinementPrompt = DefaultPrompts.AnalysisRefinement;
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
    /// When true, a second agent reviews the analysis in an isolated session and feeds
    /// findings back to the original analysis agent for refinement. This adversarial
    /// loop improves analysis quality by catching missed components, incorrect assumptions,
    /// and feasibility issues before implementation begins. Default: true.
    /// </summary>
    public bool AnalysisReviewEnabled { get; init; } = true;

    /// <summary>
    /// Prompt sent to the isolated review agent that evaluates the analysis.
    /// The agent reads .agent/analysis.md, .agent/analysis-assessment.json, and .agent/issue-context.md,
    /// then writes findings to .agent/analysis-review.md.
    /// </summary>
    public string AnalysisReviewPrompt { get; init; } = DefaultAnalysisReviewPrompt;

    /// <summary>
    /// Prompt sent back to the original analysis session instructing it to refine
    /// the analysis based on the review feedback at .agent/analysis-review.md.
    /// </summary>
    public string AnalysisRefinementPrompt { get; init; } = DefaultAnalysisRefinementPrompt;

    /// <summary>
    /// When true, refactoring proposals are reviewed by an isolated discriminator agent
    /// before issues are created. Default: true.
    /// </summary>
    public bool RefactoringReviewEnabled { get; init; } = true;

    /// <summary>
    /// When true, brain consolidation changes are reviewed by an isolated discriminator
    /// agent before being committed. Default: true.
    /// </summary>
    public bool BrainConsolidationReviewEnabled { get; init; } = true;

    /// <summary>
    /// When true, harness suggestions are reviewed by an isolated discriminator agent
    /// before being persisted. Default: true.
    /// </summary>
    public bool HarnessSuggestionsReviewEnabled { get; init; } = true;

    /// <summary>
    /// When true, the pipeline runs a baseline health check (agent environment + workspace build)
    /// after branch creation and before code analysis. Default: true.
    /// </summary>
    public bool BaselineHealthCheckEnabled { get; init; } = true;

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

    public int MaxInfrastructureRetries
    {
        get => ExternalCi.MaxInfrastructureRetries;
        init => ExternalCi = ExternalCi with { MaxInfrastructureRetries = value };
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
    /// Applies non-null project overrides to a PipelineConfiguration instance.
    /// Called BEFORE ApplyTemplateOverrides in the dispatch pipeline.
    /// Each non-null property on the project replaces the corresponding global value.
    /// Nested objects (e.g., CodeReview) use REPLACE semantics — no deep merge.
    /// </summary>
    public static PipelineConfiguration ApplyProjectOverrides(
        PipelineConfiguration config, PipelineProject? project)
    {
        if (project is null) return config;

        try
        {
            if (project.MaxRetries.HasValue)
                config = config with { MaxRetries = project.MaxRetries.Value };
            if (project.MaxAnalysisRetries.HasValue)
                config = config with { MaxAnalysisRetries = project.MaxAnalysisRetries.Value };
            if (project.AgentTimeout.HasValue)
                config = config with { AgentTimeout = project.AgentTimeout.Value };
            if (project.AnalysisPrompt is not null)
                config = config with { AnalysisPrompt = project.AnalysisPrompt };
            if (project.ImplementationPrompt is not null)
                config = config with { ImplementationPrompt = project.ImplementationPrompt };
            if (project.AnalysisReviewEnabled.HasValue)
                config = config with { AnalysisReviewEnabled = project.AnalysisReviewEnabled.Value };
            if (project.AnalysisReviewPrompt is not null)
                config = config with { AnalysisReviewPrompt = project.AnalysisReviewPrompt };
            if (project.AnalysisRefinementPrompt is not null)
                config = config with { AnalysisRefinementPrompt = project.AnalysisRefinementPrompt };
            if (project.CodeReview is not null)
                config = config with { CodeReview = project.CodeReview }; // REPLACE semantics — entire object replaced
            if (project.BaselineHealthCheckEnabled.HasValue)
                config = config with { BaselineHealthCheckEnabled = project.BaselineHealthCheckEnabled.Value };
            if (project.ExternalCiTimeout.HasValue)
                config = config with { ExternalCiTimeout = project.ExternalCiTimeout.Value };
            if (project.ExternalCiPollInterval.HasValue)
                config = config with { ExternalCiPollInterval = project.ExternalCiPollInterval.Value };
            if (project.MaxInfrastructureRetries.HasValue)
                config = config with { MaxInfrastructureRetries = project.MaxInfrastructureRetries.Value };
            if (project.StallWarningInterval.HasValue)
                config = config with { StallWarningInterval = project.StallWarningInterval.Value };
            if (project.MaxDecompositionSubIssues.HasValue)
                config = config with { MaxDecompositionSubIssues = project.MaxDecompositionSubIssues.Value };
            if (project.MaxConcurrentDecompositions.HasValue)
                config = config with { MaxConcurrentDecompositions = project.MaxConcurrentDecompositions.Value };
            if (project.DecompositionTimeout.HasValue)
                config = config with { DecompositionTimeout = project.DecompositionTimeout.Value };
            if (project.MaxOpenIssuesForContext.HasValue)
                config = config with { MaxOpenIssuesForContext = project.MaxOpenIssuesForContext.Value };
            if (project.MaxRefactoringProposals.HasValue)
                config = config with { MaxRefactoringProposals = project.MaxRefactoringProposals.Value };
            if (project.RefactoringReviewEnabled.HasValue)
                config = config with { RefactoringReviewEnabled = project.RefactoringReviewEnabled.Value };
            if (project.BrainConsolidationReviewEnabled.HasValue)
                config = config with { BrainConsolidationReviewEnabled = project.BrainConsolidationReviewEnabled.Value };
            if (project.HarnessSuggestionsReviewEnabled.HasValue)
                config = config with { HarnessSuggestionsReviewEnabled = project.HarnessSuggestionsReviewEnabled.Value };
            if (project.BlacklistedPaths is not null)
                config = config with { BlacklistedPaths = project.BlacklistedPaths };
            if (project.BlacklistMode.HasValue)
                config = config with { BlacklistMode = project.BlacklistMode.Value };
            if (project.BrainReadOnly.HasValue)
                config = config with { BrainReadOnly = project.BrainReadOnly.Value };
        }
        catch (ArgumentOutOfRangeException ex)
        {
            Log.Warning(
                "Project '{ProjectName}' (ID: {ProjectId}) has out-of-range override values — falling back to global defaults. {ErrorMessage}",
                project.Name, project.Id, ex.Message);
            return config;
        }

        return config;
    }

    /// <summary>
    /// Applies per-repo blacklist overrides from a <see cref="ProviderConfig"/>.
    /// When the provider config specifies <see cref="ProviderConfig.BlacklistedPaths"/>
    /// or <see cref="ProviderConfig.BlacklistMode"/>, they take precedence over the
    /// global pipeline configuration defaults.
    /// </summary>
    public static PipelineConfiguration ApplyBlacklistOverride(PipelineConfiguration config, ProviderConfig? repoProviderConfig)
    {
        if (repoProviderConfig is null)
            return config;

        if (repoProviderConfig.BlacklistedPaths is { Count: > 0 })
            config = config with { BlacklistedPaths = repoProviderConfig.BlacklistedPaths };
        if (repoProviderConfig.BlacklistMode is { } repoBlacklistMode)
            config = config with { BlacklistMode = repoBlacklistMode };
        return config;
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
    // Phase 2 TODO: Move template storage to per-project directories, then add [Obsolete]
    public IReadOnlyList<PipelineJobTemplate> PipelineJobTemplates { get; init; } = Array.Empty<PipelineJobTemplate>();

    /// <summary>
    /// Maximum number of refactoring proposals the agent is instructed to produce
    /// and the executor will create issues for. Controls both the prompt instruction
    /// ("Produce at most N proposals") and the issue creation cap in RefactoringExecutor.
    /// Default: 3.
    /// </summary>
    public int MaxRefactoringProposals { get; init; } = 3;

    /// <summary>
    /// Time window for git hotspot analysis in refactoring detection.
    /// Only commits within this window are counted. Default: 90 days.
    /// </summary>
    public TimeSpan HotspotAnalysisLookback { get; init; } = TimeSpan.FromDays(90);

    /// <summary>
    /// Maximum number of sub-issues per epic decomposition (range: 1–20). Default: 10.
    /// </summary>
    // TODO: This refactor from backing field to C# 13 `field` keyword changes the exception message format — verify no consumers depend on the old message.
    public int MaxDecompositionSubIssues
    {
        get => field;
        init
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(value, 1);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(value, 20);
            field = value;
        }
    } = 10;

    /// <summary>
    /// Maximum simultaneous decomposition runs. Default: 2.
    /// </summary>
    public int MaxConcurrentDecompositions { get; init; } = 2;

    /// <summary>
    /// Timeout for each decomposition phase. Default: 15 minutes.
    /// </summary>
    public TimeSpan DecompositionTimeout { get; init; } = TimeSpan.FromMinutes(15);

    /// <summary>
    /// Maximum open issues downloaded for deduplication context. Default: 50.
    /// </summary>
    public int MaxOpenIssuesForContext { get; init; } = 50;

    /// <summary>
    /// Time window for querying past refactoring proposal outcomes.
    /// Only closed issues within this window are included in the feedback context.
    /// Default: 90 days.
    /// </summary>
    public TimeSpan RefactoringOutcomeLookback { get; init; } = TimeSpan.FromDays(90);
}
