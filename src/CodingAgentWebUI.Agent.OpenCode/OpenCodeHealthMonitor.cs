using System.Text.Json;
using CodingAgentWebUI.Agent;
using Microsoft.Extensions.Hosting;
using ILogger = Serilog.ILogger;

namespace CodingAgentWebUI.Agent.OpenCode;

/// <summary>
/// Background service that periodically polls the OpenCode server's health endpoint.
/// Logs warnings when the server becomes unreachable. Only registered when the agent
/// container is configured to use the OpenCode provider.
/// </summary>
public sealed class OpenCodeHealthMonitor : BackgroundService
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger _logger;
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(5);

    public OpenCodeHealthMonitor(IHttpClientFactory httpClientFactory, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(httpClientFactory);
        _httpClientFactory = httpClientFactory;
        _logger = logger ?? Serilog.Log.Logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.Information("OpenCode health monitor started (polling every {Interval}s)", PollInterval.TotalSeconds);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(PollInterval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            await CheckHealthAsync(stoppingToken);
        }

        _logger.Information("OpenCode health monitor stopped");
    }

    /// <summary>
    /// Internal entry point for testing. Delegates to the health check logic.
    /// </summary>
    internal Task CheckHealthInternalAsync(CancellationToken ct) => CheckHealthAsync(ct);

    private async Task CheckHealthAsync(CancellationToken ct)
    {
        try
        {
            using var client = _httpClientFactory.CreateClient(AgentDefaults.OpenCodeHttpClientName);
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(5));

            var response = await client.GetAsync("/global/health", timeoutCts.Token);

            if (!response.IsSuccessStatusCode)
            {
                _logger.Warning("OpenCode health check failed: HTTP {StatusCode}", (int)response.StatusCode);
                return;
            }

            var json = await response.Content.ReadAsStringAsync(timeoutCts.Token);
            var health = JsonSerializer.Deserialize<HealthResponse>(json, OpenCodeJson.JsonOptions);

            if (health is not { Healthy: true })
            {
                _logger.Warning("OpenCode health check returned unhealthy state");
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Shutting down — don't log
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "OpenCode health check failed");
        }
    }
}
