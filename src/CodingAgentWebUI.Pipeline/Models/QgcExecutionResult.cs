using MessagePack;

namespace CodingAgentWebUI.Pipeline.Models;

/// <summary>
/// Result of executing a single Quality Gate Configuration during a pipeline run.
/// </summary>
[MessagePackObject]
public sealed record QgcExecutionResult
{
    [Key(0)]
    public required string QgcId { get; init; }

    [Key(1)]
    public required string DisplayName { get; init; }

    [Key(2)]
    public GateResult? Compilation { get; init; }

    [Key(3)]
    public GateResult? Tests { get; init; }

    [Key(4)]
    public GateResult? Coverage { get; init; }

    [Key(5)]
    public GateResult? SecurityScan { get; init; }

    /// <summary>
    /// Computed: all individual gates passed (or were not applicable).
    /// </summary>
    [IgnoreMember]
    public bool Passed => (Compilation?.Passed ?? true) && (Tests?.Passed ?? true)
        && (Coverage?.Passed ?? true) && (SecurityScan?.Passed ?? true);
}
