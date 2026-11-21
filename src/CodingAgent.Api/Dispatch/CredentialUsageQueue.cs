using System.Threading.Channels;
using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;
using Microsoft.Extensions.Hosting;

namespace CodingAgent.Api.Dispatch;

/// <summary>
/// Hands usage readings to <see cref="CredentialUsageMonitor"/> on a background loop, so the hub call that brought them
/// returns at once: SignalR runs one call per agent connection at a time, and the monitor's database work and locks
/// would otherwise hold up the agent's output lines, step reports and token refreshes, and a chat pod's next prompt.
/// One reading at a time per replica. When the queue is full the oldest reading is dropped: the next call reports again.
/// </summary>
internal sealed class CredentialUsageQueue(ICredentialUsageObserver monitor, Serilog.ILogger logger)
    : BackgroundService, ICredentialUsageObserver
{
    /// <summary>The most readings waiting at once.</summary>
    internal const int Capacity = 1000;

    private readonly Serilog.ILogger _log = logger.ForContext<CredentialUsageQueue>();

    private readonly Channel<Func<CancellationToken, Task>> _queue = Channel.CreateBounded<Func<CancellationToken, Task>>(
        new BoundedChannelOptions(Capacity) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });

    public Task ObserveAsync(Guid workItemId, IReadOnlyList<AgentRateLimitObservation> readings, CancellationToken ct)
    {
        _queue.Writer.TryWrite(token => monitor.ObserveAsync(workItemId, readings, token));
        return Task.CompletedTask;
    }

    public Task ObserveCredentialAsync(string credential, IReadOnlyList<AgentRateLimitObservation> readings, CancellationToken ct)
    {
        _queue.Writer.TryWrite(token => monitor.ObserveCredentialAsync(credential, readings, token));
        return Task.CompletedTask;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var observe in _queue.Reader.ReadAllAsync(stoppingToken))
        {
            try
            {
                await observe(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log.Warning(ex, "Usage readings could not be checked against the usage thresholds");
            }
        }
    }
}
