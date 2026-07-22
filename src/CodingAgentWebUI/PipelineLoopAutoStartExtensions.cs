using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Pipeline.Services;
using Serilog;

namespace CodingAgentWebUI;

/// <summary>
/// Extension methods for auto-starting the pipeline loop at application startup.
/// </summary>
internal static class PipelineLoopAutoStartExtensions
{
    /// <summary>
    /// Auto-starts the pipeline loop if <see cref="PipelineConfiguration.ClosedLoopAutoStart"/> is enabled.
    /// </summary>
    /// <remarks>
    /// Should be the last startup task before <c>app.Run()</c> — the loop begins processing
    /// issues immediately after starting.
    /// </remarks>
    public static async Task AutoStartPipelineLoopAsync(this WebApplication app, PipelineConfiguration pipelineConfig)
    {
        ArgumentNullException.ThrowIfNull(app);
        // TODO: Add ArgumentNullException.ThrowIfNull(pipelineConfig) — pipelineConfig is dereferenced
        // on ClosedLoopAutoStart but has no null guard (review-findings)

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
