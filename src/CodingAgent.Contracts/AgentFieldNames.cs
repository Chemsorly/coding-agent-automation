namespace CodingAgent.Contracts;

// TODO: The doc-comment below says "every AgentEntry Redis hash field name", but the AgentEntry hash also
// contains "status" (written via TransitionStatus, not UpdateAgentFieldAsync), "agentId", "hostname", and
// "labels" — none of which are in this type. These fields were intentionally excluded by the issue #3440
// scopeQuery because they are written on different paths and were never duplicated across files. Consider
// either (a) adding Status (and optionally the identity fields) to route the remaining raw literals here, or
// (b) softening the doc-comment to "every AgentEntry field written through the UpdateAgentField writer/reader
// contract (per issue #3440 scope)" to make it literally accurate. See review-findings (correctness WARNING).
/// <summary>
/// Shared string constants for AgentEntry Redis hash field names written through the
/// <c>UpdateAgentField</c> writer/reader contract (per issue #3440 scope).
/// Referenced by all writers (<see cref="CodingAgent.Pipeline.Services.IAgentRegistryService.UpdateAgentFieldAsync"/> callers)
/// and both readers (<c>AgentRegistryService</c> switch, <c>DistributedAgentRegistryService</c>).
/// Mirrors the established <see cref="AgentLabels"/> pattern for centralising wire-contract constants.
/// Note: "status", "agentId", "hostname", and "labels" are written on separate paths and are intentionally
/// out of scope for this type.
/// </summary>
public static class AgentFieldNames
{
    // ── Fields from the existing AgentGateway-internal AgentFieldNames ────────

    /// <summary>The Redis hash field that stores the currently-assigned job run ID.</summary>
    public const string ActiveJobId = "activeJobId";

    /// <summary>The Redis hash field that stores the timestamp of the last completed job.</summary>
    public const string LastJobCompletedAt = "lastJobCompletedAt";

    /// <summary>The Redis hash field that stores the timestamp when orphan recovery was applied.</summary>
    public const string OrphanRestoredAt = "orphanRestoredAt";

    // ── Fields from DistributedAgentRegistryService per-file consts ───────────

    /// <summary>The Redis hash field that stores the active chat session ID.</summary>
    public const string ActiveChatSessionId = "activeChatSessionId";

    /// <summary>The Redis hash field that stores whether the agent is administratively disabled.</summary>
    public const string Disabled = "disabled";

    /// <summary>The Redis hash field that stores the timestamp when the agent became Busy.</summary>
    public const string BusySince = "busySince";

    /// <summary>The Redis hash field that stores the timestamp when the agent disconnected.</summary>
    public const string DisconnectedAt = "disconnectedAt";

    // ── Fields used as raw literals only (no prior named const) ──────────────

    /// <summary>The Redis hash field that stores the timestamp of the last heartbeat.</summary>
    public const string LastHeartbeatAt = "lastHeartbeatAt";

    /// <summary>The Redis hash field that stores the SignalR connection ID.</summary>
    public const string ConnectionId = "connectionId";

    /// <summary>The Redis hash field that stores the timestamp when the agent first registered.</summary>
    public const string RegisteredAt = "registeredAt";
}
