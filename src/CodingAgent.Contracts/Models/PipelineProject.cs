namespace CodingAgent.Pipeline.Models;

/// <summary>
/// A named grouping entity that owns pipeline job templates and carries
/// per-project behavioral settings that override global defaults.
/// Persisted in the <c>Projects</c> table.
/// </summary>
public sealed record PipelineProject
{
    /// <summary>Unique identifier (GUID), generated on creation.</summary>
    public required string Id { get; init; }

    /// <summary>Operator-assigned display name (max 128 characters).</summary>
    public required string Name { get; init; }

    /// <summary>Optional description for documentation (max 512 characters).</summary>
    public string? Description { get; init; }

    /// <summary>Whether this project is active for polling. Default true.</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>
    /// The IDs of the templates in this project, in <see cref="TemplateOrder"/>. A template's own project is
    /// the only membership record: the store fills this list when it loads the project, and saving a project
    /// ignores it. To change membership, save the template into a project or move it.
    /// </summary>
    public IReadOnlyList<string> TemplateIds { get; init; } = [];

    /// <summary>
    /// Optional centralized issue tracker for cross-repo epic decomposition.
    /// When set, the loop additionally polls this provider for agent:epic issues.
    /// </summary>
    public string? EpicIssueProviderId { get; init; }

    /// <summary>
    /// Whether <paramref name="issueProviderId"/> is this project's epic tracker. Epics that live there are
    /// project epics: their decomposition may create sub-issues in the tracker of every template in the
    /// project. Epics in any other tracker are repo epics, limited to the tracker they live in.
    /// </summary>
    public bool IsEpicTracker(string? issueProviderId) =>
        !string.IsNullOrEmpty(EpicIssueProviderId)
        && string.Equals(EpicIssueProviderId, issueProviderId, StringComparison.Ordinal);

    // ── Behavioral overrides (null = inherit from global) ──────────────
    // Each one overrides the [ProjectOverridable] PipelineConfiguration property of the same name and is held to that
    // property's [Range]: saving a project rejects a value outside it, and the resolver skips a stored one.

    public int? MaxRetries { get; init; }
    public int? MaxAnalysisRetries { get; init; }
    public TimeSpan? AgentTimeout { get; init; }
    public string? AnalysisPrompt { get; init; }
    public string? ImplementationPrompt { get; init; }
    public bool? AnalysisReviewEnabled { get; init; }
    public string? AnalysisReviewPrompt { get; init; }
    public string? AnalysisRefinementPrompt { get; init; }
    public bool? AcceptanceCriteriaEnabled { get; init; }
    public CodeReviewOverrides? CodeReview { get; init; }
    public bool? BaselineHealthCheckEnabled { get; init; }
    public TimeSpan? ExternalCiTimeout { get; init; }
    public TimeSpan? ExternalCiPollInterval { get; init; }
    public TimeSpan? CiNotStartedTimeout { get; init; }
    public int? CiNotStartedMaxRetries { get; init; }
    public int? MaxInfrastructureRetries { get; init; }
    public TimeSpan? StallWarningInterval { get; init; }
    public int? MaxDecompositionSubIssues { get; init; }
    public int? MaxDecompositionSubIssueFiles { get; init; }
    public int? MaxOpenIssuesForContext { get; init; }
    public int? MaxRefactoringProposals { get; init; }
    public bool? RefactoringReviewEnabled { get; init; }
    public bool? BrainConsolidationReviewEnabled { get; init; }
    public IReadOnlyList<string>? BlacklistedPaths { get; init; }
    public bool? BrainReadOnly { get; init; }

    /// <summary>
    /// Override for <see cref="PipelineConfiguration.AnalysisCommitThreshold"/>.
    /// Null = inherit from global configuration.
    /// </summary>
    public int? AnalysisCommitThreshold { get; init; }

    /// <summary>
    /// Override for <see cref="PipelineConfiguration.CiCancelledMoveMaxRetries"/>.
    /// Null = inherit from global configuration.
    /// </summary>
    public int? CiCancelledMoveMaxRetries { get; init; }

    /// <summary>
    /// Override for <see cref="PipelineConfiguration.FeedbackTimeoutSeconds"/>.
    /// Null = inherit from global configuration.
    /// </summary>
    public int? FeedbackTimeoutSeconds { get; init; }

    /// <summary>
    /// Optional markdown steering content written to the agent workspace before each run.
    /// Provides persistent behavioral instructions (code style, tool preferences, constraints).
    /// </summary>
    public string? SteeringContent { get; init; }

    /// <summary>
    /// Project-level secrets injected as process-wide environment variables for every run
    /// in this project. Merged with repo-level secrets at dispatch time (repo wins on key collision).
    /// Keys must match POSIX env var pattern: [A-Za-z_][A-Za-z0-9_]*
    /// </summary>
    public Dictionary<string, string>? Secrets { get; init; }

    /// <summary>
    /// Project-level MCP servers merged with the resolved profile's MCP servers at dispatch time.
    /// Project MCPs override profile MCPs with the same Name (case-insensitive); others are added.
    /// Null = inherit profile MCPs only. Empty list = explicitly override with no project MCPs (passthrough).
    /// </summary>
    public IReadOnlyList<McpServerConfig>? McpServers { get; init; }
}
