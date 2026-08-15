namespace CodingAgentWebUI.Pipeline.Interfaces;

/// <summary>
/// Minimal leader-election abstraction for services in CodingAgentWebUI.Pipeline that
/// need to gate work on leadership without creating a circular project reference to
/// CodingAgentWebUI.Orchestration.
/// <para>
/// This interface intentionally exposes only the two members needed for the leader-wait /
/// linked-CTS pattern: <see cref="IsLeader"/> (polled every 2 s in the wait loop) and
/// <see cref="LeaderToken"/> (combined with the host stop token to cancel in-flight work
/// on leadership loss). The full <c>ILeaderElectionService</c> in Orchestration extends
/// this interface and adds event-based notification members.
/// </para>
/// </summary>
public interface ILeaderGate
{
    /// <summary>True when this instance currently holds the leader lease/lock.</summary>
    bool IsLeader { get; }

    /// <summary>
    /// Cancelled when leadership is lost or the service is stopping.
    /// Pass this token (linked with the host stop token) to in-flight work so it
    /// can be interrupted cleanly on leadership loss.
    /// </summary>
    CancellationToken LeaderToken { get; }
}
