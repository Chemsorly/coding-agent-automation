using MessagePack;

namespace CodingAgentWebUI.Pipeline.Models;

/// <summary>
/// Metadata for an image extracted from an issue body or comment.
/// Binary content is NOT included — download happens agent-side.
/// </summary>
[MessagePackObject]
public sealed record ImageReference
{
    [Key(0)]
    public required string Url { get; init; }

    [Key(1)]
    public required string AltText { get; init; }

    [Key(2)]
    public required ImageSourceType SourceType { get; init; }

    [Key(3)]
    public required int SourceIndex { get; init; }
}

/// <summary>
/// Discriminates whether an image was found in the issue/PR body or in a comment.
/// </summary>
public enum ImageSourceType : byte
{
    Body = 0,
    Comment = 1
}
