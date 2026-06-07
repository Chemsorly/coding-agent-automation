namespace CodingAgentWebUI.Pipeline.Models;

/// <summary>
/// Tracks the current status of a registered agent.
/// </summary>
public enum AgentStatus
{
    Idle,
    Busy,
    Disconnected
}

/// <summary>
/// Represents a registered agent in the orchestrator's in-memory registry.
/// Mutable properties use <c>{ get; set; }</c> for fields that change during the agent lifecycle
/// (reconnection, status transitions, job assignment, heartbeats).
/// </summary>
public sealed record AgentEntry
{
    public required string AgentId { get; init; }

    /// <summary>SignalR connection ID — mutable to support reconnection with the same agentId.</summary>
    public required string ConnectionId { get; set; }

    public required string Hostname { get; init; }

    public required string AgentType { get; init; }

    public required IReadOnlyList<string> Labels { get; init; }

    /// <summary>Current agent status — transitions between Idle, Busy, and Disconnected.</summary>
    public AgentStatus Status { get; set; } = AgentStatus.Idle;

    /// <summary>Active job ID when status is Busy, null when Idle or Disconnected without a job.</summary>
    public string? ActiveJobId { get; set; }

    /// <summary>Active chat session ID when the agent is processing a chat prompt, null otherwise.</summary>
    public string? ActiveChatSessionId { get; set; }

    public required DateTimeOffset RegisteredAt { get; init; }

    /// <summary>Updated on each heartbeat received from the agent.</summary>
    public DateTimeOffset LastHeartbeatAt { get; set; }

    /// <summary>Timestamp of the last completed job — used for FIFO agent selection.</summary>
    public DateTimeOffset? LastJobCompletedAt { get; set; }

    /// <summary>Timestamp when disconnection was detected — used for grace period calculation.</summary>
    public DateTimeOffset? DisconnectedAt { get; set; }

    /// <summary>
    /// When true, the agent is excluded from job selection (SelectAgent skips it)
    /// while allowing it to finish its current job. In-memory only, not serialized.
    /// Resets to false on agent re-registration after orchestrator restart.
    /// </summary>
    public bool Disabled { get; set; } = false;
}
