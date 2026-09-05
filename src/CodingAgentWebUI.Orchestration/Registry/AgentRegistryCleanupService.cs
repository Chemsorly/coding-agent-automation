using CodingAgentWebUI.Orchestration.Redis;
using CodingAgentWebUI.Pipeline.LeaderElection;
using ILogger = Serilog.ILogger;

namespace CodingAgentWebUI.Orchestration.Registry;

/// <summary>
/// Background service that periodically removes stale members from <c>agents:all</c> and
/// <c>agents:idle</c> whose <c>agent:{id}</c> hash key has expired (TTL elapsed without heartbeat).
///
/// <para>
/// When <see cref="ILeaderElectionService"/> is provided, only the current leader runs the sweep —
/// consistent with <c>DatabaseMaintenanceService</c> and other periodic background services.
/// When no leader-election service is injected (local dev / single-replica), every instance sweeps,
/// which is safe because all <c>SREM</c> operations are idempotent.
/// </para>
///
/// NOTE: Even when the sweep is leader-gated, <see cref="DistributedAgentRegistryService.GetIdleAgents"/>
/// gracefully skips stale set members (<c>HGETALL</c> returns empty → continue), so stale entries
/// are invisible to the dispatcher regardless of whether a cleanup sweep has run.
/// </summary>
// TODO: Restore `sealed` modifier (was inadvertently removed when inheriting RedisSetCleanupService).
// This is a concrete leaf class with no reason to be subclassed further. The `sealed` keyword is also
// required by the ClassNameRegex() scanner in LayerBoundaryTests — without it the DI registration
// guard cannot detect this class and a future accidental removal of its AddHostedService registration
// would go undetected (reintroducing the HeartbeatMonitorService class of regression). (Review: DotNetSpecialist, Correctness)
public class AgentRegistryCleanupService : RedisSetCleanupService
{
    public AgentRegistryCleanupService(
        IRedisStore store,
        ILogger logger,
        ILeaderElectionService? leaderElection = null)
        : base(store, logger, leaderElection)
    {
    }

    protected override TimeSpan SweepInterval => TimeSpan.FromMinutes(2);
    protected override string ServiceName => "AgentRegistryCleanupService";
    protected override string MembershipSetKey => "agents:all";
    protected override string HashKeyPrefix => "agent:";

    protected override async Task RemoveStaleAsync(string agentId, CancellationToken ct)
    {
        await _store.SetRemoveAsync("agents:all", agentId);
        await _store.SetRemoveAsync("agents:idle", agentId);
    }
}
