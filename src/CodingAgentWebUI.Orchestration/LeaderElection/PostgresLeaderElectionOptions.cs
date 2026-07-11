namespace CodingAgentWebUI.Orchestration.LeaderElection;

/// <summary>
/// Configuration options for Postgres advisory lock-based leader election.
/// Bound from configuration section "LeaderElection:Postgres".
/// </summary>
public sealed class PostgresLeaderElectionOptions
{
    public const string SectionName = "LeaderElection:Postgres";

    /// <summary>
    /// Interval between lock renewal (verification) checks.
    /// The lock is session-scoped so "renewal" really means verifying
    /// the connection is alive and the lock is still held.
    /// Default: 5 seconds.
    /// </summary>
    public TimeSpan RenewalInterval { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Interval between attempts to acquire the lock when not currently the leader.
    /// Default: 5 seconds.
    /// </summary>
    public TimeSpan AcquireRetryInterval { get; set; } = TimeSpan.FromSeconds(5);
}
