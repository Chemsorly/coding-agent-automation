using CodingAgentWebUI.Pipeline.Interfaces;
using Microsoft.Extensions.Hosting;

namespace CodingAgentWebUI.Services;

/// <summary>
/// Persists pipeline loop state to disk and auto-resumes the loop on startup if
/// previously active. Ensures loop mode survives pod restarts in Kubernetes.
///
/// File: <c>{ConfigBaseDirectory}/loop-state.json</c>
///
/// Startup behavior:
/// - If file contains <c>isActive: true</c> → waits configurable delay → calls StartLoopAsync()
/// - If file is missing, unreadable, or <c>isActive: false</c> → no action (current behavior preserved)
///
/// The startup delay (default 90s, configurable via PIPELINE_LOOP_STARTUP_DELAY_SECONDS)
/// prevents dispatching jobs to agent pods that may be mid-termination during rolling updates.
/// </summary>
public sealed class LoopStatePersistenceService : IHostedLifecycleService, IDisposable
{
    private readonly IPipelineLoopService _loopService;
    private readonly Serilog.ILogger _logger;
    private readonly ILoopStateStore _stateStore;
    private readonly TimeSpan _startupDelay;
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    private CancellationTokenSource? _resumeCts;

    /// <summary>Whether the service is currently in the startup delay phase before resuming the loop.</summary>
    public bool IsResuming { get; private set; }

    /// <summary>Seconds remaining in the startup delay countdown, or 0 if not resuming.</summary>
    public int ResumeCountdownSeconds { get; private set; }

    public LoopStatePersistenceService(
        IPipelineLoopService loopService,
        Serilog.ILogger logger,
        ILoopStateStore stateStore,
        TimeSpan? startupDelay = null)
    {
        _loopService = loopService;
        _logger = logger;
        _stateStore = stateStore;
        _startupDelay = startupDelay ?? ResolveStartupDelay();
    }

    // ── IHostedLifecycleService ─────────────────────────────────────────

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _loopService.OnChange += OnLoopStateChanged;
        return Task.CompletedTask;
    }

    public Task StartingAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>
    /// Called after ALL hosted services have started. Checks for persisted active state
    /// and schedules auto-resume after the configured startup delay.
    /// </summary>
    public async Task StartedAsync(CancellationToken cancellationToken)
    {
        var state = await ReadStateAsync();
        if (state is null || !state.IsActive)
        {
            _logger.Information("Loop state persistence: no active loop state found — loop will remain inactive");
            return;
        }

        _logger.Information("Loop state persistence: found active loop state (started at {StartedAt}). Resuming in {Delay}s",
            state.StartedAt, _startupDelay.TotalSeconds);

        _resumeCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _ = ResumeLoopAfterDelayAsync(_resumeCts.Token);
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _loopService.OnChange -= OnLoopStateChanged;
        _resumeCts?.Cancel();
        return Task.CompletedTask;
    }

    public Task StoppingAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StoppedAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    // ── Loop state change handler ───────────────────────────────────────

    private void OnLoopStateChanged()
    {
        // Fire-and-forget — errors are logged, not thrown
        _ = PersistCurrentStateAsync();
    }

    private async Task PersistCurrentStateAsync()
    {
        try
        {
            await _writeLock.WaitAsync();
            try
            {
                var state = new LoopState
                {
                    IsActive = _loopService.IsLoopActive,
                    StartedAt = _loopService.IsLoopActive ? DateTimeOffset.UtcNow : null,
                    StoppedAt = !_loopService.IsLoopActive ? DateTimeOffset.UtcNow : null
                };

                await _stateStore.WriteAsync(state, CancellationToken.None);
            }
            finally
            {
                _writeLock.Release();
            }
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Failed to persist loop state");
        }
    }

    // ── Auto-resume logic ───────────────────────────────────────────────

    private async Task ResumeLoopAfterDelayAsync(CancellationToken ct)
    {
        IsResuming = true;
        var totalSeconds = (int)_startupDelay.TotalSeconds;

        try
        {
            for (var remaining = totalSeconds; remaining > 0; remaining--)
            {
                ResumeCountdownSeconds = remaining;
                await Task.Delay(TimeSpan.FromSeconds(1), ct);
            }

            ResumeCountdownSeconds = 0;
            IsResuming = false;

            var started = await _loopService.StartLoopAsync();
            if (started)
                _logger.Information("Loop auto-resumed after {Delay}s startup delay", totalSeconds);
            else
                _logger.Warning("Loop auto-resume failed — StartLoopAsync returned false (no valid templates?)");
        }
        catch (OperationCanceledException)
        {
            _logger.Information("Loop auto-resume cancelled during startup delay");
            IsResuming = false;
            ResumeCountdownSeconds = 0;
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Unexpected error during loop auto-resume");
            IsResuming = false;
            ResumeCountdownSeconds = 0;
        }
    }

    // ── Store delegation ───────────────────────────────────────────────

    private async Task<LoopState?> ReadStateAsync()
    {
        try
        {
            return await _stateStore.ReadAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Failed to read loop state");
            return null;
        }
    }

    // ── Configuration ───────────────────────────────────────────────────

    /// <summary>
    /// Resolves startup delay from PIPELINE_LOOP_STARTUP_DELAY_SECONDS env var.
    /// Default: 90s. Bounds: 0–600s.
    /// </summary>
    public static TimeSpan ResolveStartupDelay()
    {
        var envValue = Environment.GetEnvironmentVariable("PIPELINE_LOOP_STARTUP_DELAY_SECONDS");
        if (string.IsNullOrWhiteSpace(envValue))
            return TimeSpan.FromSeconds(90);

        if (int.TryParse(envValue, out var seconds))
        {
            seconds = Math.Clamp(seconds, 0, 600);
            return TimeSpan.FromSeconds(seconds);
        }

        return TimeSpan.FromSeconds(90);
    }

    public void Dispose()
    {
        _resumeCts?.Dispose();
        _writeLock.Dispose();
    }
}
