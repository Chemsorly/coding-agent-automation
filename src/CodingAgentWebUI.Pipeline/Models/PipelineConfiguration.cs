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

    // ── Domain-specific sub-configurations ──────────────────────────────

    public RetryConfiguration Retry { get; init; } = new();
    public WorkspaceConfiguration Workspace { get; init; } = new();
    public ExternalCiConfiguration ExternalCi { get; init; } = new();
    public ClosedLoopConfiguration ClosedLoop { get; init; } = new();
    public AgentConfiguration Agent { get; init; } = new();
    public CommitConfiguration Commit { get; init; } = new();
    public CodeReviewConfiguration CodeReview { get; init; } = new();

    // ── Remaining top-level properties ──────────────────────────────────

    public int IssuePageSize { get; init; } = 25;
    public string AnalysisPrompt { get; init; } = DefaultAnalysisPrompt;
    public string ImplementationPrompt { get; init; } = DefaultImplementationPrompt;

    /// <summary>
    /// Records the last-used provider ID for each provider selection per pipeline.
    /// Keys: "issue", "repository", "agent", "brain", "pipeline".
    /// Values: provider config IDs.
    /// Pre-populates dropdowns on subsequent pipeline runs.
    /// </summary>
    public IReadOnlyDictionary<string, string> LastUsedProviderIds { get; init; } = new Dictionary<string, string>();

    /// <summary>
    /// List of pipeline job templates for multi-repo round-robin polling.
    /// Each template pairs an issue provider with a repository provider.
    /// When non-empty, the pipeline loop iterates through enabled templates each cycle.
    /// </summary>
    public IReadOnlyList<PipelineJobTemplate> PipelineJobTemplates { get; init; } = Array.Empty<PipelineJobTemplate>();
}
