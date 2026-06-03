namespace CodingAgentWebUI.Pipeline.Models;

/// <summary>
/// Project context for cross-repo decomposition, populated when the epic
/// originates from a project-level EpicIssueProviderId. Passed via
/// <see cref="PipelineStepContext"/> to decomposition steps.
/// </summary>
public sealed record DecompositionProjectContext
{
    /// <summary>Display name of the owning project.</summary>
    public required string ProjectName { get; init; }

    /// <summary>All repository targets in the project, used for routing instructions.</summary>
    public required IReadOnlyList<RepositoryTarget> Repositories { get; init; }
}

/// <summary>
/// Describes a single repository target within a project for cross-repo
/// decomposition context generation (.agent/project-context.md).
/// </summary>
public sealed record RepositoryTarget
{
    /// <summary>Template name — used as the routing key in targetRepository field.</summary>
    public required string TemplateName { get; init; }

    /// <summary>Human-readable description of the repository/template purpose.</summary>
    public required string Description { get; init; }

    /// <summary>Whether this template has decomposition enabled.</summary>
    public bool DecompositionEnabled { get; init; }

    /// <summary>Whether the template's repository provider is currently resolvable.</summary>
    public bool Available { get; init; } = true;

    /// <summary>Template labels (e.g., language, framework) for routing hints.</summary>
    public IReadOnlyList<string> Labels { get; init; } = [];

    /// <summary>
    /// Issue provider config ID for the template. Used by <c>CreateSubIssuesStep</c>
    /// to route decomposed issues to the correct target repository's issue tracker.
    /// </summary>
    public string? IssueProviderId { get; init; }
}
