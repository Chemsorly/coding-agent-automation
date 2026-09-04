namespace CodingAgentWebUI.Pipeline.Interfaces;

/// <summary>
/// Persistence abstraction for pipeline loop state (active/inactive + timestamps).
/// Implementations: filesystem (legacy) or database (DB mode).
/// </summary>
public interface ILoopStateStore
{
    /// <summary>Reads persisted loop state. Returns null if no state exists.</summary>
    Task<LoopState?> ReadAsync(CancellationToken ct);

    /// <summary>Persists loop state (overwrites any existing state).</summary>
    Task WriteAsync(LoopState state, CancellationToken ct);

    /// <summary>Deletes persisted loop state.</summary>
    Task DeleteAsync(CancellationToken ct);
}

/// <summary>
/// Loop state data persisted across restarts.
/// </summary>
public sealed class LoopState
{
    public bool IsActive { get; set; }
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? StoppedAt { get; set; }
}
