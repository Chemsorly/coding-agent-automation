using MessagePack;

namespace CodingAgentWebUI.Pipeline.Models;

/// <summary>
/// Project context for cross-repo decomposition, populated when the epic
/// originates from a project-level EpicIssueProviderId. Passed via
/// <see cref="PipelineStepContext"/> to decomposition steps.
/// </summary>
[MessagePackObject]
public sealed record DecompositionProjectContext
{
    /// <summary>Display name of the owning project.</summary>
    [Key(0)]
    public required string ProjectName { get; init; }

    /// <summary>All repository targets in the project, used for routing instructions.</summary>
    [Key(1)]
    public required IReadOnlyList<RepositoryTarget> Repositories { get; init; }
}

/// <summary>
/// Describes a single repository target within a project for cross-repo
/// decomposition context generation (.agent/project-context.md).
/// </summary>
[MessagePackObject]
public sealed record RepositoryTarget
{
    /// <summary>Whether the template's repository provider is currently resolvable.</summary>
    [Key(0)]
    public bool Available { get; init; } = true;

    /// <summary>Whether this template has decomposition enabled.</summary>
    [Key(1)]
    public bool DecompositionEnabled { get; init; }

    /// <summary>Human-readable description of the repository/template purpose.</summary>
    [Key(2)]
    public required string Description { get; init; }

    /// <summary>
    /// Issue provider config ID for the template. Used by <c>CreateSubIssuesStep</c>
    /// to route decomposed issues to the correct target repository's issue tracker.
    /// </summary>
    [Key(3)]
    public string? IssueProviderId { get; init; }

    /// <summary>Template labels (e.g., language, framework) for routing hints.</summary>
    [Key(4)]
    public IReadOnlyList<string> Labels { get; init; } = [];

    /// <summary>
    /// Local filesystem path (relative to workspace root) where this repository is cloned.
    /// Set by <c>CloneProjectRepositoriesStep</c> after successful clone.
    /// Null if the repo was not cloned (unavailable, clone failed, or is the primary repo at workspace root).
    /// </summary>
    [Key(5)]
    public string? LocalPath { get; set; }

    /// <summary>
    /// Repository provider config ID for the template. Used by <c>CloneProjectRepositoriesStep</c>
    /// to clone additional project repos into the workspace for cross-repo code exploration.
    /// </summary>
    [Key(6)]
    public string? RepoProviderId { get; init; }

    /// <summary>Template name — used as the routing key in targetRepository field.</summary>
    [Key(7)]
    public required string TemplateName { get; init; }
}
