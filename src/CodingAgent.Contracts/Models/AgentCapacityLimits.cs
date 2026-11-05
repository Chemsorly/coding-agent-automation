using System.ComponentModel.DataAnnotations;

namespace CodingAgent.Pipeline.Models;

/// <summary>
/// Spec 057: the agent concurrency limits an operator sets on Settings → Agents → Capacity. Stored as one JSON entry
/// (<see cref="StorageKey"/>) in the <c>KeyValueStore</c> table, next to the routing table, not in
/// <see cref="PipelineConfiguration"/>. A UI limit can only lower the Helm chart's ceiling: the lowest limit that is
/// set applies (<c>AgentCapacityRules.Effective</c>). 0 means "no limit of my own", as in Helm.
/// </summary>
public sealed record AgentCapacityLimits
{
    /// <summary>The <c>KeyValueStore</c> key of the stored limits.</summary>
    public const string StorageKey = "agent-capacity";

    /// <summary>Lowest accepted limit; 0 means no UI limit.</summary>
    public const int MinLimit = 0;

    /// <summary>Highest accepted limit.</summary>
    public const int MaxLimit = 100;

    /// <summary>Most job template limits one save may set.</summary>
    public const int MaxEntries = 100;

    /// <summary>No UI limits at all: what a deployment has before the Capacity page is first saved.</summary>
    public static readonly AgentCapacityLimits Empty = new();

    /// <summary>The UI limit for all agents together (pipeline runs and chats). 0 = none.</summary>
    [Range(MinLimit, MaxLimit)]
    public int MaxConcurrentAgents { get; init; }

    /// <summary>
    /// The UI limit per job template, keyed by the normalized template key (labels trimmed, sorted and joined with
    /// commas, e.g. <c>dotnet,dotnet10,kiro</c>; <c>""</c> is the template without labels). 0 or no entry = none.
    /// </summary>
    public IReadOnlyDictionary<string, int> MaxConcurrentByJobTemplate { get; init; }
        = new Dictionary<string, int>(StringComparer.Ordinal);
}
