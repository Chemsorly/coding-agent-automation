using MessagePack;

namespace CodingAgentWebUI.Pipeline.Models;

/// <summary>
/// Agent → Orchestrator: Completion payload for a consolidation run.
/// Reported back via SignalR when the consolidation job finishes.
/// </summary>
[MessagePackObject]
public sealed class ConsolidationJobResult
{
    [Key(0)]
    public required string JobId { get; init; }

    [Key(1)]
    public required bool Success { get; init; }

    [Key(2)]
    public string? Summary { get; init; }

    [Key(3)]
    public string? ErrorMessage { get; init; }

    /// <summary>For refactoring: the issues created from parsed proposals.</summary>
    [Key(4)]
    public IReadOnlyList<CreatedIssueInfo>? CreatedIssues { get; init; }

    /// <summary>For harness suggestions: the generated suggestions object.</summary>
    [Key(5)]
    public HarnessSuggestions? HarnessSuggestions { get; init; }
}

/// <summary>
/// Information about a GitHub issue created during a consolidation run (e.g., refactoring proposals).
/// Distinct from <see cref="CreatedIssueResult"/> which is the return type of <c>IIssueProvider.CreateIssueAsync</c>.
/// </summary>
[MessagePackObject]
public sealed record CreatedIssueInfo
{
    [Key(0)]
    public required string Identifier { get; init; }

    [Key(1)]
    public required string Title { get; init; }

    [Key(2)]
    public required string Url { get; init; }
}
