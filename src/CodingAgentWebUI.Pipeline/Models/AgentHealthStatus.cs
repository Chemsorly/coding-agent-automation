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

    /// <summary>Human-readable summary of the current status.</summary>
    public string Summary =>
        !IsExecuting ? "Idle"
        : IsProcessAlive == true ? $"Running (PID {ProcessId})"
        : IsProcessAlive == false ? $"Process exited (PID {ProcessId})"
        : "Executing (process state unknown)";
}
