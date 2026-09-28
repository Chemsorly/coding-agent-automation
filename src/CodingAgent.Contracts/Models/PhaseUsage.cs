using MessagePack;

namespace CodingAgent.Pipeline.Models;

/// <summary>
/// Accumulated tokens, cost, session count, and agent wall-clock time for a single pipeline phase.
/// </summary>
[MessagePackObject]
public sealed record PhaseUsage
{
    [Key(0)]
    public long Tokens { get; init; }

    [Key(1)]
    public decimal? Cost { get; init; }

    /// <summary>
    /// Number of individual agent CLI invocations (sessions) during this phase.
    /// Populated by <see cref="AgentStallMonitor"/> via <c>AgentResult.AgentSeconds</c>.
    /// Zero for phases that bypass the stall monitor (adversarial reviews, direct provider calls).
    /// </summary>
    [Key(2)]
    public int Sessions { get; init; }

    /// <summary>
    /// Total wall-clock time in seconds spent in agent execution during this phase.
    /// Populated by <see cref="AgentStallMonitor"/>. Zero for phases bypassing the stall monitor.
    /// </summary>
    [Key(3)]
    public double AgentSeconds { get; init; }

    /// <summary>Primary constructor matching the original positional record.</summary>
    public PhaseUsage(long Tokens, decimal? Cost, int Sessions = 0, double AgentSeconds = 0.0)
    {
        this.Tokens = Tokens;
        this.Cost = Cost;
        this.Sessions = Sessions;
        this.AgentSeconds = AgentSeconds;
    }

    // Parameterless constructor required by MessagePack deserialization.
    [SerializationConstructor]
    public PhaseUsage() { }
}
