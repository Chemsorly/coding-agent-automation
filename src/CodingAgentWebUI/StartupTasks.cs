using CodingAgentWebUI.Orchestration.Dispatch;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Pipeline.Services;
using Serilog;

namespace CodingAgentWebUI;

/// <summary>
/// Extension methods for post-build application startup tasks: consolidation cleanup,
/// run rehydration, and pipeline loop auto-start.
/// </summary>
internal static class StartupTasks
{
    /// <summary>
    /// Runs post-build startup tasks: cleans up orphaned consolidation runs, rehydrates
    /// queued runs, and auto-starts the pipeline loop if configured.
    /// </summary>
    public static async Task RunPostBuildStartupAsync(this WebApplication app, PipelineConfiguration pipelineConfig)
    {
        // Clean up orphaned consolidation runs from previous sessions
        var consolidationService = app.Services.GetRequiredService<IConsolidationService>();
        await consolidationService.CleanupOrphanedRunsAsync(CancellationToken.None);

        // Rehydrate queued consolidation runs (re-enqueue them for dispatch)
        var queuedRuns = await consolidationService.RehydrateQueuedRunsAsync(CancellationToken.None);
        if (queuedRuns.Count > 0)
        {
            var consolidationQueue = app.Services.GetRequiredService<ConsolidationQueueService>();
            foreach (var run in queuedRuns)
            {
                var job = new PendingConsolidationJob
                {
                    RunId = run.RunId,
                    Type = run.Type,
                    TemplateId = run.TemplateId,
                    WorkspacePath = Path.Combine(pipelineConfig.WorkspaceBaseDirectory, "consolidation", run.RunId),
                    RequiredLabels = run.QueuedRequiredLabels ?? [],
                    EnqueuedAt = run.StartedAtUtc
                };
                consolidationQueue.EnqueueJob(job);
            }
        }

        // Auto-start pipeline loop if configured
        if (pipelineConfig.ClosedLoopAutoStart)
        {
            var loopService = app.Services.GetRequiredService<PipelineLoopService>();
            var loopStarted = await loopService.StartLoopAsync();
            if (loopStarted)
                Log.Information("Pipeline loop auto-started (ClosedLoopAutoStart=true)");
            else
                Log.Warning("Pipeline loop auto-start requested but StartLoopAsync returned false (no valid templates?)");
        }
    }
}
