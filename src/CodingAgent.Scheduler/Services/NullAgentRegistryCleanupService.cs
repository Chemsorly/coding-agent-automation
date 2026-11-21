using CodingAgent.Orchestration.Redis;
using CodingAgent.Orchestration.Registry;
using Serilog.Core;

namespace CodingAgent.Scheduler.Services;

/// <summary>
/// No-op <see cref="AgentRegistryCleanupService"/> registered when Redis is not configured.
///
/// <para>
/// Being an <see cref="AgentRegistryCleanupService"/> (and therefore an <see cref="IHostedService"/>),
/// it keeps <c>GetRequiredService&lt;AgentRegistryCleanupService&gt;()</c> safe in every configuration.
/// <see cref="StartAsync"/> and <see cref="StopAsync"/> do nothing, so the sweep loop never starts.
/// </para>
/// </summary>
internal sealed class NullAgentRegistryCleanupService()
    : AgentRegistryCleanupService(new NullRedisStore(), Logger.None)
{
    public override Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public override Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
