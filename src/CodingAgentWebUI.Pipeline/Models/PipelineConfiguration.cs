using System.Text.Json.Serialization;

// TODO: Consider extracting TimeSpan default values (AgentTimeout, ExternalCiTimeout, etc.) into named constants per issue #238.
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
}

/// <summary>
/// Aggregate pipeline configuration composed of domain-specific sub-configs.
/// Flat property accessors are retained for JSON backward compatibility — existing
/// flat JSON files deserialize correctly via these properties, which delegate to
/// the underlying sub-config records.
/// </summary>
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

    // ── Domain-specific sub-configs ─────────────────────────────────────

    /// <summary>Retry and timeout settings.</summary>
    [JsonIgnore]
    public RetryConfiguration Retry { get; init; } = new();

    /// <summary>Workspace management settings.</summary>
    [JsonIgnore]
    public WorkspaceConfiguration Workspace { get; init; } = new();

    /// <summary>External CI integration settings.</summary>
    [JsonIgnore]
    public ExternalCiConfiguration ExternalCi { get; init; } = new();

    /// <summary>Closed-loop polling settings.</summary>
    [JsonIgnore]
    public ClosedLoopConfiguration ClosedLoop { get; init; } = new();

    /// <summary>Multi-agent and brain repository settings.</summary>
    [JsonIgnore]
    public AgentConfiguration Agent { get; init; } = new();

    /// <summary>Commit blacklisting settings.</summary>
    [JsonIgnore]
    public CommitConfiguration Commit { get; init; } = new();

    // ── Flat property accessors (JSON backward compatibility) ────────────
    // These delegate to the sub-configs and are used for JSON serialization/deserialization.

    public int MaxRetries
    {
        get => Retry.MaxRetries;
        init => Retry = Retry with { MaxRetries = value };
    }

    public int MaxAnalysisRetries
    {
        get => Retry.MaxAnalysisRetries;
        init => Retry = Retry with { MaxAnalysisRetries = value };
    }

    public TimeSpan AgentTimeout
    {
        get => Retry.AgentTimeout;
        init => Retry = Retry with { AgentTimeout = value };
    }

    public TimeSpan StallWarningInterval
    {
        get => Retry.StallWarningInterval;
        init => Retry = Retry with { StallWarningInterval = value };
    }

    public TimeSpan StallPollInterval
    {
        get => Retry.StallPollInterval;
        init => Retry = Retry with { StallPollInterval = value };
    }

    public int IssuePageSize { get; init; } = 25;
    public CodeReviewConfiguration CodeReview { get; init; } = new();
    public string AnalysisPrompt { get; init; } = DefaultAnalysisPrompt;
    public string ImplementationPrompt { get; init; } = DefaultImplementationPrompt;

    public string WorkspaceBaseDirectory
    {
        get => Workspace.WorkspaceBaseDirectory;
        init => Workspace = Workspace with { WorkspaceBaseDirectory = value };
    }

    public int FailedWorkspaceRetentionDays
    {
        get => Workspace.FailedWorkspaceRetentionDays;
        init => Workspace = Workspace with { FailedWorkspaceRetentionDays = value };
    }

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

    public TimeSpan ClosedLoopPollInterval
    {
        get => ClosedLoop.ClosedLoopPollInterval;
        init => ClosedLoop = ClosedLoop with { ClosedLoopPollInterval = value };
    }

    public int ClosedLoopMaxRunsPerCycle
    {
        get => ClosedLoop.ClosedLoopMaxRunsPerCycle;
        init => ClosedLoop = ClosedLoop with { ClosedLoopMaxRunsPerCycle = value };
    }

    public int ClosedLoopMaxConsecutivePollFailures
    {
        get => ClosedLoop.ClosedLoopMaxConsecutivePollFailures;
        init => ClosedLoop = ClosedLoop with { ClosedLoopMaxConsecutivePollFailures = value };
    }

    public TimeSpan ClosedLoopMaxBackoffInterval
    {
        get => ClosedLoop.ClosedLoopMaxBackoffInterval;
        init => ClosedLoop = ClosedLoop with { ClosedLoopMaxBackoffInterval = value };
    }

    public int ClosedLoopMaxPagesToFetch
    {
        get => ClosedLoop.ClosedLoopMaxPagesToFetch;
        init => ClosedLoop = ClosedLoop with { ClosedLoopMaxPagesToFetch = value };
    }

    public string? DefaultRequiredAgentLabels
    {
        get => Agent.DefaultRequiredAgentLabels;
        init => Agent = Agent with { DefaultRequiredAgentLabels = value };
    }

    public int BrainPushMaxRetries
    {
        get => Agent.BrainPushMaxRetries;
        init => Agent = Agent with { BrainPushMaxRetries = value };
    }

    public TimeSpan AgentDisconnectGracePeriod
    {
        get => Agent.AgentDisconnectGracePeriod;
        init => Agent = Agent with { AgentDisconnectGracePeriod = value };
    }

    public int OutputBufferCapacity
    {
        get => Agent.OutputBufferCapacity;
        init => Agent = Agent with { OutputBufferCapacity = value };
    }

    public bool BrainReadOnly
    {
        get => Agent.BrainReadOnly;
        init => Agent = Agent with { BrainReadOnly = value };
    }

    // ── Non-grouped properties ──────────────────────────────────────────

    /// <summary>
    /// Records the last-used provider ID for each provider selection per pipeline.
    /// </summary>
    public IReadOnlyDictionary<string, string> LastUsedProviderIds { get; init; } = new Dictionary<string, string>();

    /// <summary>
    /// List of pipeline job templates for multi-repo round-robin polling.
    /// </summary>
    public IReadOnlyList<PipelineJobTemplate> PipelineJobTemplates { get; init; } = Array.Empty<PipelineJobTemplate>();
}
