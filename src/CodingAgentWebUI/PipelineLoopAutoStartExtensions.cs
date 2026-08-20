using CodingAgentWebUI.Api.Client;
using CodingAgentWebUI.Pipeline.Services;
using Serilog;

namespace CodingAgentWebUI;

/// <summary>
/// Extension methods for auto-starting the pipeline loop at application startup.
/// </summary>
internal static class PipelineLoopAutoStartExtensions
{
    /// <summary>
    /// Auto-starts the pipeline loop if <see cref="CodingAgentWebUI.Pipeline.Models.PipelineConfiguration.ClosedLoopAutoStart"/> is enabled.
    /// Loads the current configuration from the Pipeline API to determine whether to auto-start.
    /// If the API is unreachable, retries with exponential backoff (max 10 minutes) rather than
    /// silently defaulting to disabled. Respects <see cref="IHostApplicationLifetime.ApplicationStopping"/>
    /// so a host shutdown during startup exits cleanly instead of looping forever.
    /// </summary>
    public static async Task AutoStartPipelineLoopAsync(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);

        // Grab the stopping token from the host lifetime so retries abort on shutdown.
        var stoppingToken = app.Lifetime.ApplicationStopping;

        // Load ClosedLoopAutoStart from the API rather than using the default PipelineConfiguration()
        // (which has ClosedLoopAutoStart=false and would silently prevent the loop from auto-starting).
        // Do NOT pass pipelineConfig from Program.cs; always fetch the current value from the API.
        var configClient = app.Services.GetRequiredService<IPipelineApiConfigClient>();
        var pipelineConfig = await LoadConfigWithRetryAsync(configClient, stoppingToken);

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

    /// <summary>
    /// Loads PipelineConfiguration from the API with exponential backoff on failure.
    /// If the API is unreachable at startup, logs a warning and retries in the background.
    /// (do NOT default to disabled). Hard limit: 10 minutes total wait. On host shutdown or
    /// after exceeding the limit, returns a default config (ClosedLoopAutoStart=false).
    /// </summary>
    private static async Task<Pipeline.Models.PipelineConfiguration> LoadConfigWithRetryAsync(
        IPipelineApiConfigClient configClient,
        CancellationToken stoppingToken)
    {
        var delays = new[] { 2, 5, 10, 30, 60, 120, 300 }; // seconds
        var attempt = 0;
        var totalWaitedSeconds = 0;
        const int MaxTotalWaitSeconds = 600;

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                return await configClient.GetPipelineConfigAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                Log.Warning("AutoStartPipelineLoopAsync: startup cancelled by host shutdown");
                return new Pipeline.Models.PipelineConfiguration();
            }
            catch (Exception ex)
            {
                var delaySec = attempt < delays.Length ? delays[attempt] : delays[^1];
                totalWaitedSeconds += delaySec;
                if (totalWaitedSeconds >= MaxTotalWaitSeconds)
                {
                    Log.Fatal(ex,
                        "AutoStartPipelineLoopAsync: API unreachable after {Total}s — defaulting to ClosedLoopAutoStart=false",
                        totalWaitedSeconds);
                    return new Pipeline.Models.PipelineConfiguration();
                }
                Log.Warning(ex,
                    "AutoStartPipelineLoopAsync: attempt {Attempt}, retrying in {Delay}s (total waited {Total}s)",
                    attempt + 1, delaySec, totalWaitedSeconds);
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(delaySec), stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    return new Pipeline.Models.PipelineConfiguration();
                }
                attempt++;
            }
        }

        return new Pipeline.Models.PipelineConfiguration();
    }
}
