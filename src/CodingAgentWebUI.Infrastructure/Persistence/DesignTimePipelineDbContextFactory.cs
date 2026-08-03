using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace CodingAgentWebUI.Infrastructure.Persistence;

/// <summary>
/// Design-time factory for EF Core migrations tooling.
/// Used by <c>dotnet ef migrations add</c> — no running application needed.
/// Connection string is irrelevant for migration generation (schema-only).
/// </summary>
/// <remarks>
/// Before running migrations locally, set the environment variable:
/// <code>
///   $env:DESIGN_TIME_CONNECTION_STRING = "Host=localhost;Database=pipeline_design;Username=postgres;Password=yourpassword"
/// </code>
/// Or use dotnet user-secrets / a local .env file to supply the value.
/// </remarks>
public class DesignTimePipelineDbContextFactory : IDesignTimeDbContextFactory<PipelineDbContext>
{
    public PipelineDbContext CreateDbContext(string[] args)
    {
        // Read connection string from environment variable — no hardcoded credentials (S2068).
        // Set DESIGN_TIME_CONNECTION_STRING before running 'dotnet ef migrations add'.
        var connectionString = Environment.GetEnvironmentVariable("DESIGN_TIME_CONNECTION_STRING");

        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException(
                "DESIGN_TIME_CONNECTION_STRING environment variable is not set. " +
                "Set it before running EF Core migration commands. Example: " +
                "Host=localhost;Database=pipeline_design;Username=postgres;Password=yourpassword");
        }

        var optionsBuilder = new DbContextOptionsBuilder<PipelineDbContext>();
        optionsBuilder.UseNpgsql(connectionString);
        return new PipelineDbContext(optionsBuilder.Options);
    }
}
