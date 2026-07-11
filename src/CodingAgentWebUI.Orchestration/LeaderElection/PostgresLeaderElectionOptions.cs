namespace CodingAgentWebUI.Orchestration.LeaderElection;

/// <summary>
/// Configuration options for Postgres advisory lock-based leader election.
/// Bound from configuration section "LeaderElection:Postgres".
/// </summary>
public sealed class PostgresLeaderElectionOptions
{
    public const string SectionName = "LeaderElection:Postgres";

    /// <summary>
    /// Interval between lock renewal/verification checks.
    /// If the lock is lost during a check (e.g., connection dropped), leadership is relinquished.
    /// Default: 5 seconds.
    /// </summary>
    public TimeSpan RenewalInterval { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Interval between attempts to acquire the lock when not currently the leader.
    /// Default: 5 seconds.
    /// </summary>
    public TimeSpan RetryInterval { get; set; } = TimeSpan.FromSeconds(5);
}
