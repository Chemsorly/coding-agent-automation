using Microsoft.Extensions.Hosting;
using Npgsql;

namespace CodingAgentWebUI.Infrastructure;

/// <summary>
/// Background service that monitors database connectivity for /readyz probes.
/// Uses a dedicated connection (Pooling=false) separate from the application pool.
/// On DB loss: marks /readyz unhealthy (503), continues retrying, resumes on recovery.
/// Never crashes the application.
/// </summary>
public sealed class DatabaseReadinessMonitor : BackgroundService
{
    private readonly DatabaseHealthState _healthState;
    private readonly string _healthCheckConnectionString;
    private readonly Serilog.ILogger _logger;
    private readonly TimeSpan _checkInterval;

    public DatabaseReadinessMonitor(
        DatabaseHealthState healthState,
        string connectionString,
        Serilog.ILogger logger,
        TimeSpan? checkInterval = null)
    {
        _healthState = healthState;
        _healthCheckConnectionString = BuildHealthCheckConnectionString(connectionString);
        _logger = logger;
        _checkInterval = checkInterval ?? TimeSpan.FromSeconds(5);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Brief startup delay so the main DB pool is established first
        await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ProbeDatabaseAsync(stoppingToken);

                if (!_healthState.IsDatabaseHealthy)
                {
                    _healthState.MarkHealthy();
                    _logger.Information("Database connectivity restored — /readyz resumed healthy");
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                if (_healthState.IsDatabaseHealthy)
                {
                    _logger.Warning(ex, "Database connectivity lost — /readyz marked unhealthy");
                    _healthState.MarkUnhealthy();
                }
                else
                {
                    _logger.Warning("Database still unreachable: {Message}", ex.Message);
                }
            }

            try
            {
                await Task.Delay(_checkInterval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task ProbeDatabaseAsync(CancellationToken ct)
    {
        await using var conn = new NpgsqlConnection(_healthCheckConnectionString);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT 1";
        cmd.CommandTimeout = 5;
        await cmd.ExecuteScalarAsync(ct);
    }

    /// <summary>
    /// Builds a health-check connection string with Pooling=false so health
    /// checks don't share the application pool.
    /// </summary>
    public static string BuildHealthCheckConnectionString(string appConnectionString)
    {
        var builder = new NpgsqlConnectionStringBuilder(appConnectionString)
        {
            Pooling = false,
            MaxPoolSize = 1
        };
        return builder.ConnectionString;
    }

    /// <summary>
    /// Normalises an application connection string: enforces Timeout=15 minimum and
    /// SslMode=Require in production when SslMode is Prefer.
    /// </summary>
    public static string NormalizeConnectionString(string connectionString, bool isProduction)
    {
        var builder = new NpgsqlConnectionStringBuilder(connectionString);

        if (builder.Timeout == 0)
            builder.Timeout = 15;

        if (isProduction && builder.SslMode == SslMode.Prefer)
            builder.SslMode = SslMode.Require;

        return builder.ConnectionString;
    }
}
