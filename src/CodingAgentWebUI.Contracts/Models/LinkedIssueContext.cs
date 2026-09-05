using MessagePack;

namespace CodingAgentWebUI.Pipeline.Models;

/// <summary>
/// Pre-fetched linked issue context, included in the job assignment for review runs.
/// </summary>
[MessagePackObject]
public sealed class LinkedIssueContext
{
    [Key(0)]
    public required string Description { get; init; }

    [Key(1)]
    public required string Identifier { get; init; }

    [Key(2)]
    public required string Title { get; init; }
}
