using AwesomeAssertions;
using CodingAgent.Api;
using CodingAgent.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace CodingAgent.Pipeline.UnitTests.Services;

/// <summary>
/// Verifies that <see cref="ApiServiceCollectionExtensions.AddApiInfrastructure"/> reads
/// <c>ASPNETCORE_ENVIRONMENT</c> and passes the derived <c>isProduction</c> flag correctly
/// to <see cref="CodingAgent.Infrastructure.DatabaseReadinessMonitor.NormalizeConnectionString"/>.
///
/// <para>
/// These tests guard against the regression identified in Issue #3757 review:
/// inverting the <c>isProduction</c> determination would silently bypass production SSL
/// enforcement (<c>SslMode=Prefer</c> would not be upgraded to <c>SslMode=Require</c>).
/// </para>
///
/// <para>
/// Placed in <see cref="EnvironmentVariablesCollection"/> to prevent parallel execution
/// with other tests that read or write process-wide environment variables.
/// </para>
/// </summary>
[Collection("EnvironmentVariables")]
[Trait("Feature", "ConnectionNormalization")]
public sealed class AddApiInfrastructureConnectionNormalizationTests
{
    /// <summary>
    /// Registers infrastructure, builds the provider, resolves the pooled
    /// <see cref="IDbContextFactory{TContext}"/>, and returns the connection string
    /// that ended up in the Npgsql options — without opening any database connection.
    /// </summary>
    private static string GetNormalizedConnectionString(string inputConnectionString)
    {
        var services = new ServiceCollection();
        services.AddApiInfrastructure(inputConnectionString);
        using var provider = services.BuildServiceProvider();
        var factory = provider.GetRequiredService<IDbContextFactory<PipelineDbContext>>();
        using var context = factory.CreateDbContext();
        // GetConnectionString() reads the configured connection string from the
        // Npgsql options extension — no live DB connection is opened.
        return context.Database.GetConnectionString()!;
    }

    [Fact]
    public void AddApiInfrastructure_WhenAspNetCoreEnvironmentIsDevelopment_DoesNotUpgradeSslMode()
    {
        var saved = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT");
        try
        {
            Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", "Development");
            var input = new NpgsqlConnectionStringBuilder
            {
                Host = "localhost",
                Database = "test",
                Username = "test",
                Password = "test",
                SslMode = SslMode.Prefer
            }.ConnectionString;

            var result = GetNormalizedConnectionString(input);

            new NpgsqlConnectionStringBuilder(result).SslMode.Should().Be(SslMode.Prefer,
                "ASPNETCORE_ENVIRONMENT=Development sets isProduction=false; " +
                "NormalizeConnectionString must not upgrade SslMode=Prefer to Require in development");
        }
        finally
        {
            Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", saved);
        }
    }

    [Theory]
    [InlineData("Production")]
    [InlineData(null)]
    public void AddApiInfrastructure_WhenAspNetCoreEnvironmentIsNotDevelopment_UpgradesSslModeToRequire(
        string? environment)
    {
        var saved = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT");
        try
        {
            Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", environment);
            var input = new NpgsqlConnectionStringBuilder
            {
                Host = "localhost",
                Database = "test",
                Username = "test",
                Password = "test",
                SslMode = SslMode.Prefer
            }.ConnectionString;

            var result = GetNormalizedConnectionString(input);

            new NpgsqlConnectionStringBuilder(result).SslMode.Should().Be(SslMode.Require,
                $"ASPNETCORE_ENVIRONMENT={(environment is null ? "(null)" : $"\"{environment}\"")} " +
                "sets isProduction=true; NormalizeConnectionString must upgrade SslMode=Prefer to Require");
        }
        finally
        {
            Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", saved);
        }
    }
}
