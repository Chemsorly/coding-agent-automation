using CodingAgentWebUI.Pipeline.Interfaces;

namespace CodingAgentWebUI.Orchestration.LeaderElection;

/// <summary>
/// Abstraction for leader election, allowing multiple backends
/// (K8s Lease, Postgres advisory lock, single-instance no-op).
/// Consumers depend on this interface rather than a concrete implementation.
/// <para>
/// Extends <see cref="ILeaderGate"/> so that services in <c>CodingAgentWebUI.Pipeline</c>
/// can accept an <c>ILeaderGate?</c> dependency without creating a circular project reference
/// back to <c>CodingAgentWebUI.Orchestration</c>.
/// </para>
/// <para>
/// <see cref="ILeaderGate.IsLeader"/> and <see cref="ILeaderGate.LeaderToken"/> are inherited
/// from <see cref="ILeaderGate"/>. This interface adds event-based notification members on top.
/// </para>
/// </summary>
public interface ILeaderElectionService : ILeaderGate
{
    /// <summary>
    /// Fires when leadership is acquired. Subscribers can start leader-only work.
    /// </summary>
    event Action? OnStartedLeading;

    /// <summary>
    /// Fires when leadership is lost. Subscribers should stop leader-only work.
    /// </summary>
    event Action? OnStoppedLeading;
}
