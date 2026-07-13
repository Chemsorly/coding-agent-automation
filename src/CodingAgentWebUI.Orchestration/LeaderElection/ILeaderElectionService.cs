namespace CodingAgentWebUI.Orchestration.LeaderElection;

/// <summary>
/// Abstraction for leader election, allowing multiple backends
/// (K8s Lease, Postgres advisory lock, single-instance no-op).
/// Consumers depend on this interface rather than a concrete implementation.
/// </summary>
public interface ILeaderElectionService
{
    /// <summary>
    /// True when this instance currently holds the leader lease/lock.
    /// </summary>
    bool IsLeader { get; }

    /// <summary>
    /// A CancellationToken that is valid while this instance is the leader.
    /// Cancelled when leadership is lost or the service is stopping.
    /// Dependent services should pass this token to their work loops.
    /// </summary>
    CancellationToken LeaderToken { get; }

    /// <summary>
    /// Fires when leadership is acquired. Subscribers can start leader-only work.
    /// </summary>
    event Action? OnStartedLeading;

    /// <summary>
    /// Fires when leadership is lost. Subscribers should stop leader-only work.
    /// </summary>
    event Action? OnStoppedLeading;
}
