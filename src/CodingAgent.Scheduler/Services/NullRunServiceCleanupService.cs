using CodingAgent.Orchestration;
using CodingAgent.Orchestration.Redis;
using Serilog.Core;

namespace CodingAgent.Scheduler.Services;

/// <summary>
/// No-op <see cref="RunServiceCleanupService"/> registered when Redis is not configured.
///
/// <para>
/// Being a <see cref="RunServiceCleanupService"/> (and therefore an <see cref="IHostedService"/>),
/// it keeps <c>GetRequiredService&lt;RunServiceCleanupService&gt;()</c> safe in every configuration.
/// <see cref="StartAsync"/> and <see cref="StopAsync"/> do nothing, so the sweep loop never starts.
/// </para>
/// </summary>
internal sealed class NullRunServiceCleanupService()
    : RunServiceCleanupService(new NullRedisStore(), Logger.None)
{
    public override Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public override Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
