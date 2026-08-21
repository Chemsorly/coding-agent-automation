using AwesomeAssertions;
using CodingAgentWebUI.Infrastructure;
using Npgsql;
using Xunit;

namespace CodingAgentWebUI.Infrastructure.UnitTests;

/// <summary>
/// Unit tests for the pure static methods on <see cref="DatabaseReadinessMonitor"/>.
/// No database connection required — all assertions operate on the parsed connection string.
/// </summary>
public sealed class DatabaseReadinessMonitorTests
{
    private const string BaseConnectionString =
        "Host=localhost;Port=5432;Database=coding_agent;Username=user;Password=pass";

    // ── BuildHealthCheckConnectionString ─────────────────────────────────────

    [Fact]
    public void BuildHealthCheckConnectionString_SetsPoolingFalse()
    {
        var result = DatabaseReadinessMonitor.BuildHealthCheckConnectionString(BaseConnectionString);

        var builder = new NpgsqlConnectionStringBuilder(result);
        builder.Pooling.Should().BeFalse("health-check connections must bypass the application pool");
    }

    [Fact]
    public void BuildHealthCheckConnectionString_SetsMaxPoolSizeOne()
    {
        var result = DatabaseReadinessMonitor.BuildHealthCheckConnectionString(BaseConnectionString);

        var builder = new NpgsqlConnectionStringBuilder(result);
        builder.MaxPoolSize.Should().Be(1);
    }

    [Fact]
    public void BuildHealthCheckConnectionString_PreservesDatabase()
    {
        var result = DatabaseReadinessMonitor.BuildHealthCheckConnectionString(BaseConnectionString);

        var builder = new NpgsqlConnectionStringBuilder(result);
        builder.Database.Should().Be("coding_agent", "database name must not be altered");
    }

    // ── NormalizeConnectionString ─────────────────────────────────────────────

    [Fact]
    public void NormalizeConnectionString_ZeroTimeout_SetsDefaultTimeout15()
    {
        // NpgsqlConnectionStringBuilder.Timeout defaults to 0 when not specified
        var cs = "Host=localhost;Database=db;Username=u;Password=p;Timeout=0";

        var result = DatabaseReadinessMonitor.NormalizeConnectionString(cs, isProduction: false);

        var builder = new NpgsqlConnectionStringBuilder(result);
        builder.Timeout.Should().Be(15, "zero timeout must be replaced with the 15-second default");
    }

    [Fact]
    public void NormalizeConnectionString_NonZeroTimeout_NotOverridden()
    {
        var cs = "Host=localhost;Database=db;Username=u;Password=p;Timeout=30";

        var result = DatabaseReadinessMonitor.NormalizeConnectionString(cs, isProduction: false);

        var builder = new NpgsqlConnectionStringBuilder(result);
        builder.Timeout.Should().Be(30, "an already-set timeout must not be replaced");
    }

    [Fact]
    public void NormalizeConnectionString_Production_SslModePrefer_UpgradesToRequire()
    {
        var cs = $"{BaseConnectionString};SSL Mode=Prefer";

        var result = DatabaseReadinessMonitor.NormalizeConnectionString(cs, isProduction: true);

        var builder = new NpgsqlConnectionStringBuilder(result);
        builder.SslMode.Should().Be(SslMode.Require,
            "SslMode=Prefer in production must be upgraded to Require");
    }

    [Fact]
    public void NormalizeConnectionString_NonProduction_SslModePrefer_NotUpgraded()
    {
        var cs = $"{BaseConnectionString};SSL Mode=Prefer";

        var result = DatabaseReadinessMonitor.NormalizeConnectionString(cs, isProduction: false);

        var builder = new NpgsqlConnectionStringBuilder(result);
        builder.SslMode.Should().Be(SslMode.Prefer,
            "SslMode=Prefer must not be upgraded in non-production environments");
    }

    [Fact]
    public void NormalizeConnectionString_Production_SslModeAlreadyRequire_Unchanged()
    {
        var cs = $"{BaseConnectionString};SSL Mode=Require";

        var result = DatabaseReadinessMonitor.NormalizeConnectionString(cs, isProduction: true);

        var builder = new NpgsqlConnectionStringBuilder(result);
        builder.SslMode.Should().Be(SslMode.Require,
            "SslMode=Require must remain unchanged in production");
    }

    [Fact]
    public void NormalizeConnectionString_Production_SslModeDisable_NotUpgraded()
    {
        var cs = $"{BaseConnectionString};SSL Mode=Disable";

        var result = DatabaseReadinessMonitor.NormalizeConnectionString(cs, isProduction: true);

        var builder = new NpgsqlConnectionStringBuilder(result);
        builder.SslMode.Should().Be(SslMode.Disable,
            "only SslMode=Prefer is upgraded; Disable must be left as-is even in production");
    }
}
