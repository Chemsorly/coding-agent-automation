using CodingAgent.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;

namespace CodingAgent.Web.E2ETests.Infrastructure;

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
                .AddInterceptors(RowVersionInterceptor.Instance)
                .Options;
            return new TestPipelineDbContext(options);
        }
    }

    /// <summary>
    /// <see cref="PipelineDbContext"/> subclass that adapts the Postgres-only model features for InMemory:
    /// the xmin <c>RowVersion</c> stays a concurrency token but is written by <see cref="RowVersionInterceptor"/>,
    /// and the filtered unique indexes, which InMemory cannot honour, are dropped.
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
                    rowVersionProp.IsConcurrencyToken = true;
                    rowVersionProp.ValueGenerated = Microsoft.EntityFrameworkCore.Metadata.ValueGenerated.Never;
                }

                foreach (var index in entityType.GetIndexes().Where(i => i.GetFilter() is not null).ToList())
                    entityType.RemoveIndex(index);
            }
        }
    }

    /// <summary>
    /// Does what Postgres does with xmin: every update of a row changes its <c>RowVersion</c>, so a writer
    /// that read the row before another writer saved gets <see cref="DbUpdateConcurrencyException"/>
    /// (InMemory checks concurrency tokens against the stored row). Without it InMemory keeps the last
    /// write, and a race Postgres rejects, such as two terminal transitions of one WorkItem, ends with the
    /// status of one writer and the label of the other.
    /// </summary>
    private sealed class RowVersionInterceptor : SaveChangesInterceptor
    {
        public static readonly RowVersionInterceptor Instance = new();

        public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
        {
            BumpRowVersions(eventData.Context);
            return result;
        }

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            BumpRowVersions(eventData.Context);
            return ValueTask.FromResult(result);
        }

        private static void BumpRowVersions(DbContext? context)
        {
            if (context is null)
                return;
            foreach (var entry in context.ChangeTracker.Entries().Where(e => e.State == EntityState.Modified))
            {
                if (entry.Metadata.FindProperty("RowVersion")?.ClrType != typeof(uint))
                    continue;
                var rowVersion = entry.Property("RowVersion");
                rowVersion.CurrentValue = (uint)rowVersion.OriginalValue! + 1;
            }
        }
    }
}
