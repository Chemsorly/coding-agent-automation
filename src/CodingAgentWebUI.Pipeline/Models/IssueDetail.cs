using MessagePack;

namespace CodingAgentWebUI.Pipeline.Models;

[MessagePackObject]
public sealed class IssueDetail
{
    [Key(0)]
    public required string Description { get; init; }

    [Key(1)]
    public required string Identifier { get; init; }

    [Key(2)]
    public required IReadOnlyList<string> Labels { get; init; }

    [Key(3)]
    public required string Title { get; init; }

    [Key(4)]
    public IReadOnlyList<ImageReference> Images { get; init; } = [];

    /// <summary>Web URL of the issue on the provider (e.g. GitHub HtmlUrl / GitLab WebUrl), or null if unknown.</summary>
    [Key(5)]
    public string? Url { get; init; }
}
