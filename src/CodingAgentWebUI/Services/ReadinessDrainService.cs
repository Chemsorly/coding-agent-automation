using Microsoft.Extensions.Hosting;

namespace CodingAgentWebUI.Services;

/// <summary>
/// Graceful shutdown drain service. On SIGTERM/host stopping:
/// 1. Marks /readyz as unhealthy (503) so Kubernetes removes the pod from Service endpoints
/// 2. Waits a configurable delay for endpoint removal propagation and in-flight request completion
/// 3. Then allows the host to proceed with ShutdownService (pipeline/agent cancellation)
///
/// Registration order: This service MUST be registered AFTER ShutdownService in DI.
/// IHostedLifecycleService.StoppingAsync fires in REVERSE registration order,
/// so this drain runs FIRST (flips readiness, waits), then ShutdownService cancels work.
///
/// Configurable via READINESS_DRAIN_DELAY_SECONDS env var (default: 15, bounds: 0–120).
/// </summary>
public sealed class ReadinessDrainService : IHostedLifecycleService
{
    private readonly ReadinessState _readinessState;
    private readonly TimeSpan _drainDelay;
    private readonly Serilog.ILogger _logger;
    private readonly TimeProvider _timeProvider;

    public ReadinessDrainService(ReadinessState readinessState, Serilog.ILogger logger, TimeSpan? drainDelay = null, TimeProvider? timeProvider = null)
    {
        _readinessState = readinessState;
        _logger = logger;
        _drainDelay = drainDelay ?? ResolveDrainDelay();
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StartingAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StartedAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StoppedAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>
    /// Marks readiness as unhealthy and waits for the drain delay before allowing
    /// the rest of the shutdown sequence (ShutdownService) to proceed.
    /// </summary>
    public async Task StoppingAsync(CancellationToken cancellationToken)
    {
        _readinessState.MarkNotReady();
        _logger.Information("Readiness drain started — /readyz now returns 503. Waiting {DrainDelay}s for endpoint removal propagation",
            _drainDelay.TotalSeconds);

        try
        {
            await Task.Delay(_drainDelay, _timeProvider, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            _logger.Warning("Readiness drain delay was cancelled — proceeding with shutdown immediately");
        }

        _logger.Information("Readiness drain complete — proceeding with ShutdownService");
    }

    /// <summary>
    /// Resolves drain delay from READINESS_DRAIN_DELAY_SECONDS env var.
    /// Default: 15s. Bounds: 0–120s.
    /// </summary>
    public static TimeSpan ResolveDrainDelay()
    {
        var envValue = Environment.GetEnvironmentVariable("READINESS_DRAIN_DELAY_SECONDS");
        if (string.IsNullOrWhiteSpace(envValue))
            return TimeSpan.FromSeconds(15);

        if (int.TryParse(envValue, out var seconds))
        {
            seconds = Math.Clamp(seconds, 0, 120);
            return TimeSpan.FromSeconds(seconds);
        }

        return TimeSpan.FromSeconds(15);
    }
}
