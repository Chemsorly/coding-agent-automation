using MessagePack;

namespace CodingAgentWebUI.Pipeline.Models;

[MessagePackObject]
public sealed class ParsedIssue
{
    [Key(0)]
    public required IReadOnlyList<string> AcceptanceCriteria { get; init; }

    [Key(1)]
    public required string RequirementsSection { get; init; }
}
