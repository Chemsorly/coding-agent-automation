namespace CodingAgentWebUI.Orchestration.LeaderElection;

/// <summary>
/// Configuration options for Postgres advisory lock-based leader election.
/// Bound from configuration section "LeaderElection:Postgres".
/// </summary>
public sealed class PostgresLeaderElectionOptions
{
    public const string SectionName = "LeaderElection:Postgres";

    /// <summary>
    /// Interval between lock renewal checks (verifying connection is alive and lock is held).
    /// Default: 5 seconds.
    /// </summary>
    public TimeSpan RenewalInterval { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Delay before retrying lock acquisition after losing leadership.
    /// Default: 5 seconds.
    /// </summary>
    public TimeSpan RetryDelay { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// The advisory lock key. A well-known constant to avoid collision with user advisory locks.
    /// Default: 0x0CAA_1EAD (212926893 in decimal, derived from "caa-leader").
    /// </summary>
    public long LockKey { get; set; } = 0x0CAA_1EAD;
}
