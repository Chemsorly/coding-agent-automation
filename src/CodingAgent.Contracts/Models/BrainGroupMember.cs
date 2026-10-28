using MessagePack;

namespace CodingAgent.Pipeline.Models;

/// <summary>
/// One member of the brain group sent with a brain consolidation assignment (spec 054, req 10.2, task 3.6).
/// </summary>
[MessagePackObject]
public sealed record BrainGroupMember
{
    [Key(0)] public string RepositoryFullName { get; init; } = "";
    [Key(1)] public string RepositoryShortName { get; init; } = "";
    [Key(2)] public string? ProjectName { get; init; }
    [Key(3)] public IReadOnlyList<string> StackLabels { get; init; } = [];
}
