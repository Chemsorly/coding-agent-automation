using CodingAgentWebUI.Orchestration.Redis;
using CodingAgentWebUI.Pipeline.LeaderElection;
using ILogger = Serilog.ILogger;

namespace CodingAgentWebUI.Orchestration;

/// <summary>
/// Background service that periodically removes stale members from <c>runs:active</c> whose
/// <c>run:{id}</c> hash key has expired.
///
/// <para>
/// When <see cref="ILeaderElectionService"/> is provided, only the current leader runs the sweep.
/// When no leader-election service is injected (local dev / single-replica), every instance sweeps,
/// which is safe because all <c>SREM</c> operations are idempotent.
/// </para>
///
/// <para>
/// A repair path for orphaned run hashes (Lua script crash between SREM and EXPIREAT) is
/// planned but not yet implemented. See issue for tracking.
/// </para>
/// </summary>
// TODO: Restore `sealed` modifier (was inadvertently removed when inheriting RedisSetCleanupService).
// This is a concrete leaf class with no reason to be subclassed further. The `sealed` keyword is also
// required by the ClassNameRegex() scanner in LayerBoundaryTests — without it the DI registration
// guard cannot detect this class and a future accidental removal of its AddHostedService registration
// would go undetected (reintroducing the HeartbeatMonitorService class of regression). (Review: DotNetSpecialist, Correctness)
public class RunServiceCleanupService : RedisSetCleanupService
{
    public RunServiceCleanupService(
        IRedisStore store,
        ILogger logger,
        ILeaderElectionService? leaderElection = null)
        : base(store, logger, leaderElection)
    {
    }

    protected override TimeSpan SweepInterval => TimeSpan.FromMinutes(5);
    protected override string ServiceName => "RunServiceCleanupService";
    protected override string MembershipSetKey => "runs:active";
    protected override string HashKeyPrefix => "run:";

    protected override async Task RemoveStaleAsync(string runId, CancellationToken ct)
    {
        await _store.SetRemoveAsync("runs:active", runId);
    }

    // TODO: orphan-hash repair path — scan for run:{id} hashes not in runs:active and apply
    // a short TTL (handles Lua crash between SREM and EXPIREAT). Requires IDatabase.SCAN
    // support on IRedisStore; tracked separately.
}
