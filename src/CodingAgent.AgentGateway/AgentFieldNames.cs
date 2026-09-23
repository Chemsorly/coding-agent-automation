namespace CodingAgent.AgentGateway;

/// <summary>
/// Shared string constants for AgentEntry field names used in fire-and-forget Redis updates.
/// Referenced by both <see cref="AgentIdleTransitioner"/> and <see cref="AgentJobLifecycleService"/>.
/// </summary>
internal static class AgentFieldNames
{
    internal const string ActiveJobId = "activeJobId";
    internal const string LastJobCompletedAt = "lastJobCompletedAt";
    internal const string OrphanRestoredAt = "orphanRestoredAt";
}
