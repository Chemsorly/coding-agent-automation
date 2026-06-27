using CodingAgentWebUI.Services;
using Npgsql;

namespace CodingAgentWebUI.UnitTests.Services;

public class DatabaseHealthTests
{
    #region DatabaseHealthState Tests

    [Fact]
    public void DatabaseHealthState_StartsHealthy()
    {
        var state = new DatabaseHealthState();
        Assert.True(state.IsDatabaseHealthy);
    }

    [Fact]
    public void DatabaseHealthState_MarkUnhealthy_FlipsState()
    {
        var state = new DatabaseHealthState();
        state.MarkUnhealthy();
        Assert.False(state.IsDatabaseHealthy);
    }

    [Fact]
    public void DatabaseHealthState_MarkHealthy_RestoresState()
    {
        var state = new DatabaseHealthState();
        state.MarkUnhealthy();
        Assert.False(state.IsDatabaseHealthy);
        state.MarkHealthy();
        Assert.True(state.IsDatabaseHealthy);
    }

    [Fact]
    public void DatabaseHealthState_MultipleMarkUnhealthy_Idempotent()
    {
        var state = new DatabaseHealthState();
        state.MarkUnhealthy();
        state.MarkUnhealthy();
        Assert.False(state.IsDatabaseHealthy);
    }

    #endregion

    #region Connection String Normalization Tests

    [Fact]
    public void NormalizeConnectionString_SetsTimeout15_WhenZero()
    {
        var input = "Host=localhost;Database=test;Timeout=0";
        var result = DatabaseReadinessMonitor.NormalizeConnectionString(input, isProduction: false);

        var builder = new NpgsqlConnectionStringBuilder(result);
        Assert.Equal(15, builder.Timeout);
    }

    [Fact]
    public void NormalizeConnectionString_PreservesCustomTimeout()
    {
        var input = "Host=localhost;Database=test;Timeout=30";
        var result = DatabaseReadinessMonitor.NormalizeConnectionString(input, isProduction: false);

        var builder = new NpgsqlConnectionStringBuilder(result);
        Assert.Equal(30, builder.Timeout);
    }

    [Fact]
    public void NormalizeConnectionString_EnforcesSslMode_ForProduction()
    {
        var input = "Host=localhost;Database=test";
        var result = DatabaseReadinessMonitor.NormalizeConnectionString(input, isProduction: true);

        var builder = new NpgsqlConnectionStringBuilder(result);
        Assert.Equal(SslMode.Require, builder.SslMode);
    }

    [Fact]
    public void NormalizeConnectionString_DoesNotForceSsl_ForDevelopment()
    {
        var input = "Host=localhost;Database=test";
        var result = DatabaseReadinessMonitor.NormalizeConnectionString(input, isProduction: false);

        var builder = new NpgsqlConnectionStringBuilder(result);
        // Should remain at default (Prefer)
        Assert.Equal(SslMode.Prefer, builder.SslMode);
    }

    [Fact]
    public void NormalizeConnectionString_PreservesExplicitSslMode()
    {
        // If user already set SslMode=Disable, don't override
        var input = "Host=localhost;Database=test;SslMode=Disable";
        var result = DatabaseReadinessMonitor.NormalizeConnectionString(input, isProduction: true);

        var builder = new NpgsqlConnectionStringBuilder(result);
        // Only Prefer → Require upgrade happens; explicit values are preserved
        Assert.Equal(SslMode.Disable, builder.SslMode);
    }

    #endregion

    #region BuildHealthCheckConnectionString Tests

    [Fact]
    public void BuildHealthCheckConnectionString_DisablesPooling()
    {
        var input = "Host=localhost;Database=test;Pooling=true;MaxPoolSize=100";
        var result = DatabaseReadinessMonitor.BuildHealthCheckConnectionString(input);

        var builder = new NpgsqlConnectionStringBuilder(result);
        Assert.False(builder.Pooling);
        Assert.Equal(1, builder.MaxPoolSize);
    }

    [Fact]
    public void BuildHealthCheckConnectionString_PreservesHostAndDatabase()
    {
        var input = "Host=db.example.com;Port=5433;Database=myapp;Username=admin;Password=secret";
        var result = DatabaseReadinessMonitor.BuildHealthCheckConnectionString(input);

        var builder = new NpgsqlConnectionStringBuilder(result);
        Assert.Equal("db.example.com", builder.Host);
        Assert.Equal(5433, builder.Port);
        Assert.Equal("myapp", builder.Database);
        Assert.Equal("admin", builder.Username);
        Assert.Equal("secret", builder.Password);
    }

    #endregion
}
