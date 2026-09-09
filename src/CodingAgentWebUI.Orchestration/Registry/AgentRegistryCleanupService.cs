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
public sealed class AgentRegistryCleanupService : RedisSetCleanupService
{
    private static readonly string[] AgentSetKeys = ["agents:all", "agents:idle"];
    private readonly TimeSpan _sweepInterval;

    public AgentRegistryCleanupService(
        IRedisStore store,
        ILogger logger,
        ILeaderElectionService? leaderElection = null,
        TimeSpan? sweepInterval = null)
        : base(store, logger, leaderElection)
    {
        _sweepInterval = sweepInterval ?? TimeSpan.FromMinutes(2);
    }

    protected override string ScanSetKey => "agents:all";
    protected override string HashKeyPrefix => "agent";
    protected override IReadOnlyList<string> RemovalSetKeys => AgentSetKeys;
    protected override TimeSpan SweepInterval => _sweepInterval;
}
