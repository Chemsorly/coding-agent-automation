using CodingAgentWebUI.Pipeline.Interfaces;

namespace CodingAgentWebUI.Pipeline.Models;

/// <summary>
/// Bundles the common parameters needed by all agent phase execution methods.
/// Replaces the 10-13 positional parameters on <see cref="IAgentPhaseExecutor"/> methods.
/// </summary>
public sealed record AgentPhaseContext : PipelineContextBase
{
    /// <summary>The issue being worked on.</summary>
    public required IssueDetail Issue { get; init; }

    /// <summary>Parsed issue with structured requirements/acceptance criteria.</summary>
    public required ParsedIssue ParsedIssue { get; init; }
}
