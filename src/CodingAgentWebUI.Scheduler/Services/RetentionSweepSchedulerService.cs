using CodingAgentWebUI.Api.Client;
using CodingAgentWebUI.Pipeline.Interfaces;
using Microsoft.Extensions.Hosting;
using Serilog;
using ILogger = Serilog.ILogger;

namespace CodingAgentWebUI.Scheduler.Services;

/// <summary>
/// Background service that triggers the API's retention sweep on a configurable interval.
/// Only the leader Scheduler replica triggers the sweep. The API is stateless — it always
/// executes when called (Spec 049: API leader election removed).
/// </summary>
public sealed class RetentionSweepSchedulerService : BackgroundService
{
    private readonly ISchedulerApiClient _apiClient;
    private readonly ILeaderGate? _leaderGate;
    private readonly ILogger _logger;
    private readonly TimeSpan _interval;

    public RetentionSweepSchedulerService(
        ISchedulerApiClient apiClient,
        ILeaderGate? leaderGate,
        ILogger logger,
        TimeSpan? interval = null)
    {
        _apiClient = apiClient;
        _leaderGate = leaderGate;
        _logger = logger.ForContext<RetentionSweepSchedulerService>();
        _interval = interval ?? TimeSpan.FromMinutes(60);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.Information("RetentionSweepSchedulerService started — interval {Interval}", _interval);

        using var timer = new PeriodicTimer(_interval);

        while (true)
        {
            try
            {
                if (!await timer.WaitForNextTickAsync(stoppingToken))
                    break;
            }
            catch (OperationCanceledException)
            {
                break;
            }

            // Only the leader Scheduler replica triggers the sweep
            if (_leaderGate is { IsLeader: false })
            {
                _logger.Debug("RetentionSweepSchedulerService: skipping tick — not the leader");
                continue;
            }

            try
            {
                var result = await _apiClient.TriggerRetentionSweepAsync(stoppingToken);
                _logger.Information(
                    "Retention sweep complete: staleWi={StaleWi}, staleRuns={StaleRuns}, " +
                    "staleConsolidation={StaleConsolidation}, retentionRuns={RetentionRuns}, retentionWi={RetentionWi}",
                    result.StaleWorkItemsDeleted, result.StalePipelineRunsDeleted,
                    result.StaleConsolidationRunsDeleted, result.RetentionPipelineRunsDeleted,
                    result.RetentionWorkItemsDeleted);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.Warning(ex, "Retention sweep failed — will retry next interval");
            }
        }
    }
}
