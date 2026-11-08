using MessagePack;

namespace CodingAgent.Pipeline.Models;

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

    /// <summary>Whether the issue's author is on the repository. Missing in a work item stored before this field, it reads false.</summary>
    [Key(3)]
    public bool IsAuthorVerified { get; init; }
}
