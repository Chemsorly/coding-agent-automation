namespace CodingAgentWebUI.Pipeline.Models;

/// <summary>
/// Snapshot of the agent provider's current execution state.
/// Returned by <see cref="Interfaces.IAgentProvider.GetHealthStatus"/>.
/// </summary>
public sealed record AgentHealthStatus
{
    /// <summary>Whether an agent execution is currently in progress.</summary>
    public required bool IsExecuting { get; init; }

    /// <summary>OS process ID of the running agent, if available.</summary>
    public int? ProcessId { get; init; }

    /// <summary>Whether the underlying OS process is still alive. Null if no execution is active.</summary>
    public bool? IsProcessAlive { get; init; }

    /// <summary>Timestamp of the last output line received from the agent process.</summary>
    public DateTime? LastOutputTime { get; init; }

    /// <summary>
    /// Session-level status reported by the agent backend (e.g., OpenCode's session.status SSE event).
    /// Values: "idle", "busy", "retry". Null when unavailable or not applicable.
    /// </summary>
    public string? SessionStatus { get; init; }

    /// <summary>
    /// Diagnostic message from the agent backend when in a retry/error state.
    /// Populated from the session.status "retry" event's message field.
    /// </summary>
    public string? SessionStatusMessage { get; init; }

    /// <summary>
    /// Aggregated status of all sessions (including child/subagent sessions).
    /// Only populated by providers that support session status polling (e.g., OpenCode).
    /// Format: human-readable summary like "4 sessions: 2 retry, 1 busy, 1 idle".
    /// </summary>
    public string? AllSessionsSummary { get; init; }

    /// <summary>Human-readable summary of the current status.</summary>
    public string Summary =>
        !IsExecuting ? "Idle"
        : IsProcessAlive == true ? $"Running (PID {ProcessId})"
        : IsProcessAlive == false ? $"Process exited (PID {ProcessId})"
        : "Executing (process state unknown)";
}
