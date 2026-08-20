using CodingAgentWebUI.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;

namespace CodingAgentWebUI.E2ETests.Infrastructure;

/// <summary>
/// Swaps the monolith's Npgsql-backed <see cref="PipelineDbContext"/> registrations for an
/// EF InMemory provider.
///
/// Spec 041 made PostgreSQL mandatory and Spec 045 left <c>AddPooledDbContextFactory</c> in place
/// for <c>KubernetesWorkDistributor</c> / <c>KubernetesJobCleanup</c>, so the monolith opens a
/// real connection during startup unless the registration is replaced. Without this the host
/// throws <c>Failed to connect to 127.0.0.1:5432</c> before any test runs.
/// </summary>
internal static class E2EInMemoryDatabase
{
    /// <summary>
    /// Removes every Npgsql-backed <see cref="PipelineDbContext"/> registration and installs an
    /// InMemory factory over <paramref name="databaseName"/>.
    /// </summary>
    public static void Install(IServiceCollection services, string databaseName)
    {
        RemoveDbContextRegistrations(services);
        services.AddSingleton<IDbContextFactory<PipelineDbContext>>(
            new InMemoryDbContextFactory(databaseName));
    }

    private static void RemoveDbContextRegistrations(IServiceCollection services)
    {
        var toRemove = services
            .Where(d => d.ServiceType == typeof(IDbContextFactory<PipelineDbContext>)
                     || d.ServiceType == typeof(PipelineDbContext)
                     || d.ServiceType.Name.Contains("DbContextPool", StringComparison.Ordinal)
                     || d.ServiceType == typeof(DbContextOptions<PipelineDbContext>)
                     || d.ServiceType == typeof(DbContextOptions))
            .ToList();

        foreach (var d in toRemove)
            services.Remove(d);
    }

    private sealed class InMemoryDbContextFactory : IDbContextFactory<PipelineDbContext>
    {
        private readonly string _dbName;
        public InMemoryDbContextFactory(string dbName) => _dbName = dbName;

        public PipelineDbContext CreateDbContext()
        {
            var options = new DbContextOptionsBuilder<PipelineDbContext>()
                .UseInMemoryDatabase(_dbName)
                .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
                .Options;
            return new TestPipelineDbContext(options);
        }
    }

    /// <summary>
    /// <see cref="PipelineDbContext"/> subclass that drops the Postgres-only model features
    /// (xmin RowVersion concurrency tokens, filtered unique indexes) InMemory cannot honour.
    /// </summary>
    private sealed class TestPipelineDbContext : PipelineDbContext
    {
        public TestPipelineDbContext(DbContextOptions<PipelineDbContext> options) : base(options) { }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);

            foreach (var entityType in modelBuilder.Model.GetEntityTypes())
            {
                var rowVersionProp = entityType.FindProperty("RowVersion");
                if (rowVersionProp is not null)
                {
                    rowVersionProp.IsConcurrencyToken = false;
                    rowVersionProp.ValueGenerated = Microsoft.EntityFrameworkCore.Metadata.ValueGenerated.Never;
                }

                foreach (var index in entityType.GetIndexes().Where(i => i.GetFilter() is not null).ToList())
                    entityType.RemoveIndex(index);
            }
        }
    }
}
