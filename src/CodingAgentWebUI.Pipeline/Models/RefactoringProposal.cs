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
}
