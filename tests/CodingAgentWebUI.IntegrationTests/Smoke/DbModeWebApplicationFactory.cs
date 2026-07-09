using CodingAgentWebUI.Infrastructure.Locking;
using CodingAgentWebUI.Infrastructure.Persistence;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Moq;
using Serilog;

namespace CodingAgentWebUI.IntegrationTests.Smoke;

/// <summary>
/// WebApplicationFactory that boots the app in DB mode (SignalR work distribution)
/// with InMemory EF Core instead of real PostgreSQL. Validates that all DI wiring,
/// service registration, and mode-dependent paths work correctly without a real database.
/// 
/// This catches the class of bugs found during the Postgres introduction:
/// - Services accidentally resolving to filesystem instead of DB-backed implementations
/// - Missing interface registrations in DB mode
/// - UI components crashing when FeatureFlags.IsDatabaseMode is true
/// - ConfigurationStore wiring (PostgresConfigurationStore vs JsonConfigurationStore)
/// </summary>
public sealed class DbModeWebApplicationFactory : WebApplicationFactory<Program>
{
    private readonly string _dbName = $"DbModeIntegration-{Guid.NewGuid()}";

    /// <summary>
    /// Provides direct access to the InMemory DbContext for seeding test data.
    /// </summary>
    public PipelineDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<PipelineDbContext>()
            .UseInMemoryDatabase(_dbName)
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        return new TestPipelineDbContext(options);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            // Clean up process-global env vars to prevent cross-test pollution
            Environment.SetEnvironmentVariable("Database__Host", null);
            Environment.SetEnvironmentVariable("Database__Port", null);
            Environment.SetEnvironmentVariable("Database__Username", null);
            Environment.SetEnvironmentVariable("Database__Password", null);
            Environment.SetEnvironmentVariable("Database__Name", null);
            Environment.SetEnvironmentVariable("Database__SslMode", null);
            Environment.SetEnvironmentVariable("Database__MigrateOnStartup", null);
            Environment.SetEnvironmentVariable("Database__SkipStartupInit", null);
            Environment.SetEnvironmentVariable("WorkDistribution__Mode", null);
            Environment.SetEnvironmentVariable("AGENT_API_KEY", null);
        }

        base.Dispose(disposing);
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        // Set environment variables BEFORE host builds — these are read by the default
        // config builder and affect early decisions in Program.cs (Database:Host check).
        Environment.SetEnvironmentVariable("Database__Host", "localhost");
        Environment.SetEnvironmentVariable("Database__Port", "5432");
        Environment.SetEnvironmentVariable("Database__Username", "test");
        Environment.SetEnvironmentVariable("Database__Password", "test");
        Environment.SetEnvironmentVariable("Database__Name", "test_db");
        Environment.SetEnvironmentVariable("Database__SslMode", "Disable");
        Environment.SetEnvironmentVariable("Database__MigrateOnStartup", "false");
        Environment.SetEnvironmentVariable("Database__SkipStartupInit", "true");
        Environment.SetEnvironmentVariable("WorkDistribution__Mode", "SignalR");
        Environment.SetEnvironmentVariable("AGENT_API_KEY", "test-api-key");

        // Reset Serilog's global logger to a fresh bootstrap state.
        // This prevents "The logger is already frozen" when multiple WebApplicationFactory
        // instances are created in the same process — each Build() call freezes the
        // ReloadableLogger, so we need a new one for each factory invocation.
        Serilog.Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Warning()
            .WriteTo.Console()
            .CreateBootstrapLogger();

        builder.UseEnvironment("Development");

        builder.ConfigureServices(services =>
        {
            // Reduce shutdown timeout
            services.Configure<HostOptions>(o => o.ShutdownTimeout = TimeSpan.FromSeconds(5));

            // Remove all hosted services to prevent background loops in tests
            services.RemoveAll<IHostedService>();

            // Remove the real Npgsql DbContext factory and replace with InMemory
            RemoveDbContextRegistrations(services);

            // Register InMemory EF Core factory
            services.AddSingleton<IDbContextFactory<PipelineDbContext>>(
                new InMemoryDbContextFactory(_dbName));

            // Replace the distributed lock provider with InProcess (real one uses Postgres advisory locks)
            services.RemoveAll<IDistributedLockProvider>();
            services.AddDistributedLockProvider(null);

            // Replace DatabaseHealthState with a pre-healthy instance
            services.RemoveAll<DatabaseHealthState>();
            var healthState = new DatabaseHealthState();
            services.AddSingleton(healthState);

            // Replace IProviderFactory with a mock (no real GitHub/Kiro CLI)
            services.RemoveAll<IProviderFactory>();
            services.AddSingleton(new Mock<IProviderFactory>().Object);

            // Replace IQualityGateValidator with a mock
            services.RemoveAll<IQualityGateValidator>();
            services.AddSingleton(new Mock<IQualityGateValidator>().Object);

            // Register a no-op IDatabaseProbe so DatabaseStartupService skips real SQL connectivity check
            services.AddSingleton<IDatabaseProbe>(new NoOpDatabaseProbe());
        });
    }

    private static void RemoveDbContextRegistrations(IServiceCollection services)
    {
        // Remove IDbContextFactory<PipelineDbContext>
        var factoryDescriptors = services
            .Where(d => d.ServiceType == typeof(IDbContextFactory<PipelineDbContext>))
            .ToList();
        foreach (var d in factoryDescriptors) services.Remove(d);

        // Remove scoped PipelineDbContext (registered as factory accessor)
        var scopedDescriptors = services
            .Where(d => d.ServiceType == typeof(PipelineDbContext))
            .ToList();
        foreach (var d in scopedDescriptors) services.Remove(d);

        // Remove DbContextPool if registered
        var poolDescriptors = services
            .Where(d => d.ServiceType.Name.Contains("DbContextPool"))
            .ToList();
        foreach (var d in poolDescriptors) services.Remove(d);

        // Remove DbContextOptions
        var optionsDescriptors = services
            .Where(d => d.ServiceType == typeof(DbContextOptions<PipelineDbContext>)
                     || d.ServiceType == typeof(DbContextOptions))
            .ToList();
        foreach (var d in optionsDescriptors) services.Remove(d);
    }

    // ── Test Infrastructure ──────────────────────────────────────────────

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
    /// PipelineDbContext subclass that disables Postgres-specific features
    /// (RowVersion concurrency tokens, filtered indexes) for InMemory compatibility.
    /// </summary>
    private sealed class TestPipelineDbContext : PipelineDbContext
    {
        public TestPipelineDbContext(DbContextOptions<PipelineDbContext> options) : base(options) { }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);

            // Remove RowVersion concurrency token config
            foreach (var entityType in modelBuilder.Model.GetEntityTypes())
            {
                var rowVersionProp = entityType.FindProperty("RowVersion");
                if (rowVersionProp != null)
                {
                    rowVersionProp.IsConcurrencyToken = false;
                    rowVersionProp.ValueGenerated = Microsoft.EntityFrameworkCore.Metadata.ValueGenerated.Never;
                }
            }

            // Remove filter-based unique indexes (not supported by InMemory)
            foreach (var entityType in modelBuilder.Model.GetEntityTypes())
            {
                var indexesToRemove = entityType.GetIndexes()
                    .Where(i => i.GetFilter() != null)
                    .ToList();
                foreach (var index in indexesToRemove)
                    entityType.RemoveIndex(index);
            }
        }
    }

    /// <summary>
    /// No-op database probe that always succeeds — bypasses real SQL connectivity check in tests.
    /// </summary>
    private sealed class NoOpDatabaseProbe : IDatabaseProbe
    {
        public Task ProbeAsync(CancellationToken ct) => Task.CompletedTask;
    }
}
