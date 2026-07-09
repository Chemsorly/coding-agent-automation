using CodingAgentWebUI.Pipeline.Interfaces;
using Microsoft.Extensions.Hosting;

namespace CodingAgentWebUI.Services;

/// <summary>
/// Handles graceful shutdown using <see cref="IHostedLifecycleService.StoppingAsync"/>.
/// Replaces the synchronous <c>ApplicationStopping.Register</c> callback that used
/// <c>.GetAwaiter().GetResult()</c> and blocked the host shutdown thread.
/// </summary>
/// <remarks>
/// Requirements: 12.1 (no GetAwaiter().GetResult()), 12.2 (15s timeout), 12.3 (non-blocking).
/// </remarks>
public sealed class ShutdownService : IHostedLifecycleService
{
    private readonly TimeSpan _shutdownTimeout;
    private readonly TimeProvider _timeProvider;

    private readonly ILifecycleShutdownAction _lifecycle;
    private readonly IOrchestrationShutdownAction _orchestration;
    private readonly IShutdownSignal _shutdownSignal;
    private readonly Serilog.ILogger _logger;

    public ShutdownService(
        ILifecycleShutdownAction lifecycle,
        IOrchestrationShutdownAction orchestration,
        IShutdownSignal shutdownSignal,
        Serilog.ILogger logger,
        TimeSpan? shutdownTimeout = null,
        TimeProvider? timeProvider = null)
    {
        _lifecycle = lifecycle;
        _orchestration = orchestration;
        _shutdownSignal = shutdownSignal;
        _logger = logger;
        _shutdownTimeout = shutdownTimeout ?? TimeSpan.FromSeconds(15);
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StartingAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StartedAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StoppedAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>
    /// Async shutdown logic with 15-second timeout. Cancels active pipeline and agent runs,
    /// then proceeds with shutdown even if label swaps haven't completed.
    /// Uses a linked CancellationToken so abandoned work is cancelled when the timeout fires.
    /// </summary>
    public async Task StoppingAsync(CancellationToken cancellationToken)
    {
        _logger.Information("Graceful shutdown initiated — cancelling active runs (timeout: {Timeout}s)", _shutdownTimeout.TotalSeconds);

        // Link a timeout-based CTS with the host cancellation token so that
        // ExecuteShutdownAsync is cancelled both on timeout AND host abort.
        // This prevents abandoned tasks from racing against DI container disposal.
        using var timeoutCts = new CancellationTokenSource(_shutdownTimeout, _timeProvider);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
            timeoutCts.Token, cancellationToken);

        try
        {
            await ExecuteShutdownAsync(linkedCts.Token);
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested)
        {
            _logger.Warning("Graceful shutdown timed out after {Timeout}s — proceeding with shutdown", _shutdownTimeout.TotalSeconds);
        }
        catch (OperationCanceledException)
        {
            _logger.Warning("Graceful shutdown was cancelled — proceeding with shutdown");
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Unexpected error during graceful shutdown");
        }
    }

    private async Task ExecuteShutdownAsync(CancellationToken ct)
    {
        // Signal shutdown FIRST — prevents drain service from dispatching new jobs
        // that would immediately get cancelled by the label-swap pass below.
        _shutdownSignal.SignalShutdown();
        _logger.Information("Shutdown signal raised — new dispatches blocked");

        // Cancel active pipeline run if one is in progress
        if (_lifecycle.IsRunning)
        {
            try
            {
                await _lifecycle.CancelPipelineAsync().WaitAsync(ct);
                _logger.Information("Pipeline cancellation completed");
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                _logger.Warning("Pipeline cancellation aborted — shutdown timeout reached");
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Failed to cancel active pipeline run — continuing shutdown");
            }
        }

        // Cancel active agent runs and perform label swaps
        try
        {
            await _orchestration.CancelActiveAgentRunsAsync().WaitAsync(ct);
            _logger.Information("Agent run cancellation completed");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            _logger.Warning("Agent run cancellation aborted — shutdown timeout reached");
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Failed to cancel active agent runs — continuing shutdown");
        }
    }
}
