using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Pipeline.Services.Steps;

namespace CodingAgentWebUI.Agent;

/// <summary>
/// Encapsulates the finally-block cleanup logic from pipeline execution:
/// CTS disposal, environment secret cleanup, workspace deletion, and reporter disposal.
/// Extracted from <see cref="LocalPipelineExecutor"/> to enable isolated testing.
/// </summary>
internal static class PipelineCleanup
{
    /// <summary>
    /// Runs all cleanup steps sequentially. Error handling mirrors the original code:
    /// only workspace deletion is wrapped in try/catch; other operations propagate exceptions.
    /// </summary>
    public static async Task RunAsync(
        CancellationTokenSource? localCts,
        PipelineStepContext? stepContext,
        PipelineRun run,
        PipelineSignalRReporter reporter,
        Serilog.ILogger logger)
    {
        localCts?.Dispose();

        // Clean up injected environment secrets
        if (stepContext?.InjectedSecretKeys is { Count: > 0 })
        {
            foreach (var key in stepContext.InjectedSecretKeys)
                Environment.SetEnvironmentVariable(key, null);
            logger.Debug("Cleaned up {Count} injected secret keys", stepContext.InjectedSecretKeys.Count);
        }

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
