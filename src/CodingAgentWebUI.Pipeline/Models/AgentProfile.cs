using MessagePack;

namespace CodingAgentWebUI.Pipeline.Models;

/// <summary>
/// A named configuration entity that maps a set of match labels to a specific agent provider config.
/// During job dispatch, the orchestrator resolves which profile matches the selected agent
/// and sends that profile's provider config.
/// </summary>
[MessagePackObject]
public sealed record AgentProfile
{
    [Key(0)]
    public string Id { get; init; } = Guid.NewGuid().ToString();

    [Key(1)]
    public required string DisplayName { get; init; }

    [Key(2)]
    public IReadOnlyList<string> MatchLabels { get; init; } = [];

    [Key(3)]
    public required string AgentProviderConfigId { get; init; }

    [Key(4)]
    public bool Enabled { get; init; } = true;

    [Key(5)]
    public int Priority { get; init; } = 0;
}
