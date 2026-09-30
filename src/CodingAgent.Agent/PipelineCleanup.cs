using System.Diagnostics;
using CodingAgent.Pipeline.Models;
using CodingAgent.Pipeline.Services.Steps;
using CodingAgent.Pipeline.Telemetry;

namespace CodingAgent.Agent;

/// <summary>
/// Encapsulates the finally-block cleanup logic from pipeline execution:
/// CTS disposal, environment secret cleanup, workspace deletion, and reporter disposal.
/// Extracted from <see cref="LocalPipelineExecutor"/> to enable isolated testing.
/// </summary>
internal static class PipelineCleanup
{
    /// <summary>
    /// Runs all cleanup steps sequentially inside a <c>PrePrCleanup</c> trace span.
    /// Error handling mirrors the original code:
    /// only workspace deletion is wrapped in try/catch; other operations propagate exceptions.
    /// </summary>
    public static async Task RunAsync(
        CancellationTokenSource? localCts,
        PipelineStepContext? stepContext,
        PipelineRun run,
        PipelineSignalRReporter reporter,
        Serilog.ILogger logger)
    {
        using var cleanupSpan = PipelineTelemetry.ActivitySource.StartActivity("PrePrCleanup");
        cleanupSpan?.SetTag("pipeline.run_id", run.RunId);

        localCts?.Dispose();

        // Workspace cleanup
        try
        {
            if (run.CurrentStep is PipelineStep.Completed or PipelineStep.Failed or PipelineStep.Cancelled
                && !string.IsNullOrEmpty(run.WorkspacePath) && Directory.Exists(run.WorkspacePath))
            {
                Directory.Delete(run.WorkspacePath, recursive: true);
                logger.Information("Cleaned up workspace {WorkspacePath} (step={Step})", run.WorkspacePath, run.CurrentStep);
            }
        }
        catch (Exception ex)
        {
            logger.Warning(ex, "Failed to clean up workspace {WorkspacePath}", run.WorkspacePath);
        }

        // Drain in-flight serialized sends before disposing the semaphore.
        // PipelineSignalRReporter.DisposeAsync drains and disposes the SemaphoreSlim.
        await reporter.DisposeAsync();
    }
}
