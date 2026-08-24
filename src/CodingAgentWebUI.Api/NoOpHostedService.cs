using Microsoft.Extensions.Hosting;

namespace CodingAgentWebUI.Api;

/// <summary>
/// No-op hosted service used as a placeholder when an optional background service
/// (e.g., AgentRegistryCleanupService) is not configured (Redis absent).
/// </summary>
internal sealed class NoOpHostedService : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
