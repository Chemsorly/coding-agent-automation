using CodingAgentWebUI.Api.Client;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Pipeline.Services;
using Serilog;

namespace CodingAgentWebUI.Scheduler;

/// <summary>
/// Auto-start logic for the pipeline loop at Scheduler startup.
/// Copied from PipelineLoopAutoStartExtensions in CodingAgentWebUI (Spec 047 Task 4.3).
/// That file is deleted in Task 5.7; this is the canonical version going forward.
/// </summary>
internal static class SchedulerAutoStartExtensions
{
    /// <summary>
    /// Reads ClosedLoopAutoStart from the API and auto-starts the loop if enabled.
    /// Retries with exponential backoff (max 10 minutes) if the API is unreachable.
    /// Respects ApplicationStopping so a shutdown during startup exits cleanly.
    /// </summary>
    public static async Task AutoStartSchedulerLoopAsync(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var stoppingToken = app.Lifetime.ApplicationStopping;
        var configClient = app.Services.GetRequiredService<IPipelineApiConfigClient>();
        var timeProvider = app.Services.GetRequiredService<TimeProvider>();
        var pipelineConfig = await LoadConfigWithRetryAsync(configClient, timeProvider, stoppingToken);

        if (pipelineConfig.ClosedLoopAutoStart)
        {
            var loopService = app.Services.GetRequiredService<PipelineLoopService>();
            var started = await loopService.StartLoopAsync();
            if (started)
                Log.Information("Scheduler: pipeline loop auto-started (ClosedLoopAutoStart=true)");
            else
                Log.Warning("Scheduler: loop auto-start requested but StartLoopAsync returned false (no valid templates?)");
        }
        else
        {
            Log.Information("Scheduler: pipeline loop auto-start skipped (ClosedLoopAutoStart=false)");
        }
    }

    /// <summary>
    /// Loads PipelineConfiguration from the API with exponential backoff.
    /// Hard limit: 10 minutes. Returns a default config on timeout or shutdown.
    /// Delays: [2, 5, 10, 30, 60, 120, 300] seconds.
    /// </summary>
    private static async Task<PipelineConfiguration> LoadConfigWithRetryAsync(
        IPipelineApiConfigClient configClient,
        TimeProvider timeProvider,
        CancellationToken stoppingToken)
    {
        var delays = new[] { 2, 5, 10, 30, 60, 120, 300 };
        var attempt = 0;
        var totalWaitedSeconds = 0;
        const int MaxTotalWaitSeconds = 600;

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                return await configClient.GetPipelineConfigAsync(stoppingToken);
            }
            catch (OperationCanceledException oce) when (stoppingToken.IsCancellationRequested)
            {
                Log.Warning(oce, "Scheduler AutoStart: cancelled by host shutdown");
                return new PipelineConfiguration();
            }
            catch (Exception ex)
            {
                var delaySec = attempt < delays.Length ? delays[attempt] : delays[^1];
                totalWaitedSeconds += delaySec;
                if (totalWaitedSeconds >= MaxTotalWaitSeconds)
                {
                    Log.Fatal(ex,
                        "Scheduler AutoStart: API unreachable after {Total}s — defaulting to ClosedLoopAutoStart=false",
                        totalWaitedSeconds);
                    return new PipelineConfiguration();
                }
                Log.Warning(ex,
                    "Scheduler AutoStart: attempt {Attempt}, retrying in {Delay}s (total waited {Total}s)",
                    attempt + 1, delaySec, totalWaitedSeconds);
                try { await Task.Delay(TimeSpan.FromSeconds(delaySec), timeProvider, stoppingToken); }
                catch (OperationCanceledException) { return new PipelineConfiguration(); }
                attempt++;
            }
        }

        return new PipelineConfiguration();
    }
}
