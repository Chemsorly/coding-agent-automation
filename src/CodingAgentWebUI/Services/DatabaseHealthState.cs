namespace CodingAgentWebUI.Services;

/// <summary>
/// Thread-safe database health state. Tracks whether DB is reachable.
/// Used by /readyz endpoint to report 503 on DB loss.
/// Separate from ReadinessState which handles graceful shutdown only.
/// </summary>
public sealed class DatabaseHealthState
{
    private volatile bool _isDatabaseHealthy = true;

    /// <summary>
    /// True when DB is reachable. False after connectivity loss detected.
    /// </summary>
    public bool IsDatabaseHealthy => _isDatabaseHealthy;

    /// <summary>
    /// Mark DB as unreachable. /readyz will return 503.
    /// </summary>
    public void MarkUnhealthy() => _isDatabaseHealthy = false;

    /// <summary>
    /// Mark DB as recovered. /readyz resumes 200.
    /// </summary>
    public void MarkHealthy() => _isDatabaseHealthy = true;
}
