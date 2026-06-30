using Microsoft.Extensions.Configuration;
using Npgsql;

namespace CodingAgentWebUI.Services;

/// <summary>
/// Builds a PostgreSQL connection string from individual configuration components.
/// Returns null when DB mode is not configured (Host not set).
/// </summary>
public static class DatabaseConnectionResolver
{
    /// <summary>
    /// Builds a connection string from <c>Database:Host</c>, <c>Database:Port</c>,
    /// <c>Database:Username</c>, <c>Database:Password</c>, <c>Database:Name</c>,
    /// and optionally <c>Database:SslMode</c>.
    /// Returns null if <c>Database:Host</c> is not configured.
    /// </summary>
    public static string? Resolve(IConfiguration configuration)
    {
        var host = configuration.GetValue<string>("Database:Host");
        if (string.IsNullOrEmpty(host))
            return null;

        var builder = new NpgsqlConnectionStringBuilder
        {
            Host = host,
            Port = configuration.GetValue<int?>("Database:Port") ?? 5432,
            Username = configuration.GetValue<string>("Database:Username") ?? "postgres",
            Password = configuration.GetValue<string>("Database:Password") ?? "",
            Database = configuration.GetValue<string>("Database:Name") ?? "coding_agent_automation"
        };

        // Allow explicit SslMode override via config (e.g. Disable for in-cluster Postgres without TLS)
        var sslModeStr = configuration.GetValue<string>("Database:SslMode");
        if (!string.IsNullOrEmpty(sslModeStr) && Enum.TryParse<SslMode>(sslModeStr, ignoreCase: true, out var sslMode))
        {
            builder.SslMode = sslMode;
        }

        return builder.ConnectionString;
    }
}
