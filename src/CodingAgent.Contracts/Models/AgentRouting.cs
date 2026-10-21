using System.Text.Json.Serialization;
using MessagePack;

namespace CodingAgent.Pipeline.Models;

/// <summary>
/// Which agent profile runs a task type, with which model and effort (spec 053). <see cref="Model"/> is always set;
/// <c>auto</c> lets the tool choose. <see cref="Effort"/> <c>auto</c> is the tool's default.
/// </summary>
[MessagePackObject]
public sealed record AgentRoute(
    [property: Key(0)] string ProfileId,
    [property: Key(1)] string Model,
    [property: Key(2)] string Effort);

/// <summary>
/// The global routing table: the default route plus one optional route per task type. Stored as one JSON entry
/// (key <see cref="StorageKey"/>) apart from the pipeline configuration; only the Routing endpoints write it.
/// </summary>
public sealed record AgentRouting
{
    /// <summary>Key of the routing table in the key-value store.</summary>
    public const string StorageKey = "agent-routing";

    /// <summary>The route of every task type without its own row. Required once any agent profile exists.</summary>
    public AgentRoute? Default { get; init; }

    /// <summary>Routes that replace <see cref="Default"/> for one task type.</summary>
    public IReadOnlyDictionary<WorkItemTaskType, AgentRoute> ByTaskType { get; init; } = new Dictionary<WorkItemTaskType, AgentRoute>();

    /// <summary>An empty table: no default route and no task rows. Every work item is refused until a default is set.</summary>
    public static AgentRouting Empty { get; } = new();
}

/// <summary>Where a resolved route came from.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<AgentRouteSource>))]
public enum AgentRouteSource
{
    /// <summary>The repository's own row for the task type (on its pipeline job template).</summary>
    Repository,

    /// <summary>The global row for the task type.</summary>
    GlobalTaskRow,

    /// <summary>The global default route.</summary>
    GlobalDefault,

    /// <summary>A work item created before spec 053, routed when an agent claimed it.</summary>
    Claim,

    /// <summary>A work item a previous-release API stored without a selector, routed by the dispatcher.</summary>
    Dispatch,
}

/// <summary>
/// The route a work item is pinned to when it is created. <see cref="Tool"/> is the job template's tool at creation
/// and is used for telemetry only; the claim reads the tool from the job template again.
/// </summary>
public sealed record AgentRouteSnapshot(
    string ProfileId,
    string ProfileName,
    string Tool,
    string Model,
    string Effort,
    AgentRouteSource Source);

/// <summary>Display texts for routing, shared by refusal texts, validation messages and log lines.</summary>
public static class AgentRouteLabels
{
    /// <summary>The name of the default row.</summary>
    public const string DefaultRow = "Default";

    /// <summary>The name a task type has on the Routing page.</summary>
    public static string TaskType(WorkItemTaskType taskType) => taskType switch
    {
        WorkItemTaskType.Implementation => "Implementation",
        WorkItemTaskType.Review => "PR review",
        WorkItemTaskType.Decomposition => "Decomposition",
        WorkItemTaskType.Triage => "Triage",
        WorkItemTaskType.Consolidation => "Consolidation",
        _ => ((int)taskType).ToString(System.Globalization.CultureInfo.InvariantCulture),
    };

    /// <summary>How a route source is named in texts and in the <c>RouteSource</c> log property.</summary>
    public static string Source(AgentRouteSource source) => source switch
    {
        AgentRouteSource.Repository => "repository",
        AgentRouteSource.GlobalTaskRow => "global task row",
        AgentRouteSource.GlobalDefault => "global default",
        AgentRouteSource.Claim => "claim",
        AgentRouteSource.Dispatch => "dispatch",
        _ => "unknown",
    };
}
