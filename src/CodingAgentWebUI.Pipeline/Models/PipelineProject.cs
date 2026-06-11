namespace CodingAgentWebUI.Pipeline.Models;

/// <summary>
/// A named grouping entity that owns PipelineJobTemplates and carries
/// per-project behavioral settings that override global defaults.
/// Persisted as individual JSON files at {ConfigDir}/pipeline/projects/{Id}.json.
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

    /// <summary>Ordered list of template IDs belonging to this project.</summary>
    public IReadOnlyList<string> TemplateIds { get; init; } = [];

    /// <summary>
    /// Optional centralized issue tracker for cross-repo epic decomposition.
    /// When set, the loop additionally polls this provider for agent:epic issues.
    /// </summary>
    public string? EpicIssueProviderId { get; init; }

    // ── Behavioral overrides (null = inherit from global) ──────────────

    public int? MaxRetries { get; init; }
    public int? MaxAnalysisRetries { get; init; }
    public TimeSpan? AgentTimeout { get; init; }
    public string? AnalysisPrompt { get; init; }
    public string? ImplementationPrompt { get; init; }
    public bool? AnalysisReviewEnabled { get; init; }
    public string? AnalysisReviewPrompt { get; init; }
    public string? AnalysisRefinementPrompt { get; init; }
    public bool? AcceptanceCriteriaEnabled { get; init; }
    public CodeReviewConfiguration? CodeReview { get; init; }
    public bool? BaselineHealthCheckEnabled { get; init; }
    public TimeSpan? ExternalCiTimeout { get; init; }
    public TimeSpan? ExternalCiPollInterval { get; init; }
    public int? MaxInfrastructureRetries { get; init; }
    public TimeSpan? StallWarningInterval { get; init; }
    public int? MaxDecompositionSubIssues { get; init; }
    public int? MaxConcurrentDecompositions { get; init; }
    public TimeSpan? DecompositionTimeout { get; init; }
    public int? MaxOpenIssuesForContext { get; init; }
    public int? MaxRefactoringProposals { get; init; }
    public bool? RefactoringReviewEnabled { get; init; }
    public bool? BrainConsolidationReviewEnabled { get; init; }
    public bool? HarnessSuggestionsReviewEnabled { get; init; }
    public IReadOnlyList<string>? BlacklistedPaths { get; init; }
    public bool? BrainReadOnly { get; init; }

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
}
