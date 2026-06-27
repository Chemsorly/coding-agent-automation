using Npgsql;

namespace CodingAgentWebUI.Services;

/// <summary>
/// Background service that monitors database connectivity for the /readyz probe.
/// Uses a DEDICATED connection (Pooling=false) separate from app pool.
/// On DB loss: marks /readyz unhealthy (503), continues retrying, resumes on recovery.
/// Never crashes the application.
/// </summary>
public sealed class DatabaseReadinessMonitor : BackgroundService
{
    private readonly DatabaseHealthState _healthState;
    private readonly string _healthCheckConnectionString;
    private readonly Serilog.ILogger _logger;
    private readonly TimeSpan _checkInterval;

    /// <summary>
    /// Creates a new DatabaseReadinessMonitor.
    /// </summary>
    /// <param name="healthState">Shared health state for /readyz endpoint.</param>
    /// <param name="connectionString">Application connection string (will be modified for health check).</param>
    /// <param name="logger">Logger instance.</param>
    /// <param name="checkInterval">Optional override for polling interval (default 5s).</param>
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
        // Wait briefly for startup to complete before beginning health monitoring
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

    /// <summary>
    /// Performs a one-shot DB health check using a dedicated connection.
    /// Used by /healthz endpoint for liveness verification.
    /// </summary>
    public async Task<bool> CheckHealthAsync(CancellationToken ct)
    {
        try
        {
            await ProbeDatabaseAsync(ct);
            return true;
        }
        catch
        {
            return false;
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
    /// Builds a health-check-specific connection string with Pooling=false, MaxPoolSize=1.
    /// This ensures health checks use a DEDICATED connection separate from the app pool.
    /// </summary>
    internal static string BuildHealthCheckConnectionString(string appConnectionString)
    {
        var builder = new NpgsqlConnectionStringBuilder(appConnectionString)
        {
            Pooling = false,
            MaxPoolSize = 1
        };
        return builder.ConnectionString;
    }

    /// <summary>
    /// Ensures the app connection string includes required parameters:
    /// - Timeout=15 (connection acquisition timeout)
    /// - SslMode=Require for production TLS
    /// Returns the normalized connection string.
    /// </summary>
    public static string NormalizeConnectionString(string connectionString, bool isProduction)
    {
        var builder = new NpgsqlConnectionStringBuilder(connectionString);

        // Npgsql default Timeout is 15s — ensure it's explicitly set.
        // If user hasn't customized it (default is 15), leave as-is.
        // Only force 15 if somehow 0 (disabled).
        if (builder.Timeout == 0)
        {
            builder.Timeout = 15;
        }

        // Enforce SslMode=Require for production
        if (isProduction && builder.SslMode == SslMode.Prefer)
        {
            builder.SslMode = SslMode.Require;
        }

        return builder.ConnectionString;
    }
}
