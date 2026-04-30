using MessagePack;

namespace CodingAgentWebUI.Pipeline.Models;

/// <summary>
/// A named entity that defines a set of specialized review agents keyed by a set of MatchLabels.
/// Applied to jobs whose required labels intersect with the configuration's match labels.
/// Configurations with empty MatchLabels act as global fallbacks and always match.
/// </summary>
[MessagePackObject]
public sealed record ReviewerConfiguration
{
    [Key(0)]
    public string Id { get; init; } = Guid.NewGuid().ToString();

    [Key(1)]
    public required string DisplayName { get; init; }

    [Key(2)]
    public IReadOnlyList<string> MatchLabels { get; init; } = [];

    [Key(3)]
    public required IReadOnlyList<ReviewAgent> Agents { get; init; }

    [Key(4)]
    public bool Enabled { get; init; } = true;

    [Key(5)]
    public int ExecutionOrder { get; init; } = 0;
}

/// <summary>
/// A single specialized reviewer within a <see cref="ReviewerConfiguration"/>,
/// consisting of a name and a prompt. Executes sequentially within its parent configuration.
/// </summary>
[MessagePackObject]
public sealed record ReviewAgent
{
    [Key(0)]
    public required string Name { get; init; }

    [Key(1)]
    public required string Prompt { get; init; }
}
