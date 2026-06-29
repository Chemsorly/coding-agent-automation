using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace CodingAgentWebUI.Infrastructure.Persistence;

/// <summary>
/// Design-time factory for EF Core migrations tooling.
/// Used by <c>dotnet ef migrations add</c> — no running application needed.
/// Connection string is irrelevant for migration generation (schema-only).
/// </summary>
public class DesignTimePipelineDbContextFactory : IDesignTimeDbContextFactory<PipelineDbContext>
{
    public PipelineDbContext CreateDbContext(string[] args)
    {
        var optionsBuilder = new DbContextOptionsBuilder<PipelineDbContext>();
        // Dummy connection string — migrations are schema-only, no actual DB connection needed at generation time.
        optionsBuilder.UseNpgsql("Host=localhost;Database=pipeline_design;Username=postgres;Password=postgres");
        return new PipelineDbContext(optionsBuilder.Options);
    }
}
