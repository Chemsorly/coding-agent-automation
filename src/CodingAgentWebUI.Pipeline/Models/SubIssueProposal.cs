namespace CodingAgentWebUI.Pipeline.Models;

/// <summary>
/// Represents a single sub-issue parsed from the agent's JSON output.
/// Corresponds to one file in .agent/sub-issues/{NN}-{slug}.json.
/// </summary>
public sealed record SubIssueProposal
{
    /// <summary>Sub-issue title (max 256 chars).</summary>
    public required string Title { get; init; }

    /// <summary>Markdown-formatted issue body.</summary>
    public required string Body { get; init; }

    /// <summary>
    /// Title-based references to other sub-issues this depends on.
    /// Resolved to #N format during sequential creation.
    /// </summary>
    public required IReadOnlyList<string> Dependencies { get; init; }

    /// <summary>Additional labels beyond the auto-applied agent:next.</summary>
    public required IReadOnlyList<string> Labels { get; init; }

    /// <summary>
    /// Target repository for cross-repo decomposition routing.
    /// Must match a template Name within the project. Null = use dispatching template's provider.
    /// </summary>
    public string? TargetRepository { get; init; }
}
