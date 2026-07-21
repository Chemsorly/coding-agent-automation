namespace CodingAgentWebUI.Pipeline.Models;

/// <summary>
/// A single refactoring proposal produced by the agent.
/// Parsed from the .agent/refactoring-proposals.json file in the workspace.
/// </summary>
public sealed class RefactoringProposal
{
    public required string Title { get; init; }
    public required IReadOnlyList<string> AffectedFiles { get; init; }
    public required string Description { get; init; }
    public required string Rationale { get; init; }
    public IReadOnlyList<string>? Prerequisites { get; init; }
    public string? EstimatedEffort { get; init; }
    public string? RiskLevel { get; init; }
    public string? Technique { get; init; }
    public string? Category { get; init; }

    /// <summary>
    /// Evidence sources that support this proposal. Prefixed by type:
    /// "tool:" (linter/compiler output), "hotspot:" (git frequency),
    /// "code-reading:" (manual inspection), "grep:" (pattern search),
    /// "usage-search:" (reference count). Multi-source = higher confidence.
    /// </summary>
    public IReadOnlyList<string>? EvidenceSources { get; init; }

    /// <summary>
    /// Title-based references to other proposals in the same batch that this proposal depends on.
    /// Resolved to "Depends on #N" lines during sequential issue creation via <see cref="CodingAgentWebUI.Pipeline.Services.DependencyResolver"/>.
    /// </summary>
    public IReadOnlyList<string>? DependsOn { get; init; }

    /// <summary>
    /// Proposal-specific acceptance criteria validated by the review agent.
    /// Each entry becomes a checkbox in the GitHub issue body.
    /// Must be verifiable from the PR diff — no subjective or unmeasurable claims.
    /// </summary>
    public IReadOnlyList<string>? AcceptanceCriteria { get; init; }
}
