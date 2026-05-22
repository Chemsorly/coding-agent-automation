namespace CodingAgentWebUI.Pipeline.Models;

/// <summary>
/// Pre-fetched linked issue context, included in the job assignment for review runs.
/// </summary>
public sealed class LinkedIssueContext
{
    public required string Identifier { get; init; }
    public required string Title { get; init; }
    public required string Description { get; init; }
}
