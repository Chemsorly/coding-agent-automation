using CodingAgent.Orchestration.Redis;
using CodingAgent.Orchestration.Registry;
using CodingAgent.Pipeline.LeaderElection;
using ILogger = Serilog.ILogger;

namespace CodingAgent.Orchestration;

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
public sealed class RunServiceCleanupService : RedisSetCleanupService
{
    private static readonly string[] RunSetKeys = ["runs:active"];
    private readonly TimeSpan _sweepInterval;

    public RunServiceCleanupService(
        IRedisStore store,
        ILogger logger,
        ILeaderElectionService? leaderElection = null,
        TimeSpan? sweepInterval = null)
        : base(store, logger, leaderElection)
    {
        _sweepInterval = sweepInterval ?? TimeSpan.FromMinutes(5);
    }

    protected override string ScanSetKey => "runs:active";
    protected override string HashKeyPrefix => "run";
    protected override IReadOnlyList<string> RemovalSetKeys => RunSetKeys;
    protected override TimeSpan SweepInterval => _sweepInterval;
}
