using CodingAgent.Api;
using CodingAgent.Infrastructure;
using CodingAgent.Infrastructure.Locking;
using CodingAgent.Infrastructure.Persistence;
using CodingAgent.Kubernetes;
using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.LeaderElection;
using CodingAgent.Pipeline.Models;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Moq;
using Serilog;

namespace CodingAgent.Api.IntegrationTests;

/// <summary>
/// xUnit collection for tests that require <c>Consolidation:UnifiedDispatch:Enabled=true</c>.
/// <c>DisableParallelization = true</c> prevents env-var races with the shared
/// <see cref="ApiIntegrationTestCollection"/> factory (following the AgentHubGateCollection pattern).
/// </summary>
[CollectionDefinition("UnifiedDispatchCollection", DisableParallelization = true)]
public sealed class UnifiedDispatchCollection : ICollectionFixture<UnifiedDispatchWebApplicationFactory> { }

/// <summary>
/// WebApplicationFactory for integration tests that run with
/// <c>Consolidation:UnifiedDispatch:Enabled=true</c>.
///
/// <para>
/// <see cref="ApiWebApplicationFactory"/> is <c>sealed</c> and cannot be subclassed.
/// This factory replicates the same test infrastructure (InMemory EF, mocked services)
/// and adds the unified-dispatch flag via <c>builder.ConfigureAppConfiguration</c> —
/// the only correct way to inject configuration without relying on environment variables
/// that <see cref="ApiWebApplicationFactory.Dispose"/> does not clean up.
/// </para>
/// </summary>
public sealed class UnifiedDispatchWebApplicationFactory : WebApplicationFactory<Program>
{
    private readonly string _dbName = $"UnifiedDispatch-{Guid.NewGuid():N}";

    /// <summary>
    /// Creates a fresh InMemory DbContext for seeding test data directly.
    /// </summary>
    public PipelineDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<PipelineDbContext>()
            .UseInMemoryDatabase(_dbName)
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        return new UnifiedDispatchTestPipelineDbContext(options);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            // Note: do NOT clear the shared Database__ or AGENT_API_KEY env vars here —
            // the shared ApiWebApplicationFactory in ApiIntegrationTestCollection may still
            // be running when this factory disposes. Those env vars are safe to leave at
            // their test values (same values used by both factories).
            // TODO: Mirror ApiWebApplicationFactory's env-var cleanup here (null out Database__* and
            // AGENT_API_KEY). If disposal order changes so UnifiedDispatchWebApplicationFactory
            // outlives ApiWebApplicationFactory, the shared vars will be left set with stale test
            // values for any subsequent host construction in the same process. See review finding
            // on UnifiedDispatchWebApplicationFactory.cs:74.
        }

        base.Dispose(disposing);
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        // Set environment variables BEFORE host builds — same values as ApiWebApplicationFactory.
        Environment.SetEnvironmentVariable("Database__Host", "localhost");
        Environment.SetEnvironmentVariable("Database__Port", "5432");
        Environment.SetEnvironmentVariable("Database__Username", "test");
        Environment.SetEnvironmentVariable("Database__Password", "test");
        Environment.SetEnvironmentVariable("Database__Name", "test_db");
        Environment.SetEnvironmentVariable("Database__SslMode", "Disable");
        Environment.SetEnvironmentVariable("Database__MigrateOnStartup", "false");
        Environment.SetEnvironmentVariable("Database__SkipStartupInit", "true");
        Environment.SetEnvironmentVariable("AGENT_API_KEY", ApiWebApplicationFactory.ApiKey);

        Serilog.Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Warning()
            .CreateBootstrapLogger();

        builder.UseEnvironment("Development");

        // Inject the unified-dispatch flag via AddInMemoryCollection so it is layered
        // on top of the environment-variable config source without leaking into process
        // env vars that Dispose() would need to clean up.
        builder.ConfigureAppConfiguration(config =>
            config.AddInMemoryCollection(new[]
            {
                new KeyValuePair<string, string?>("Consolidation:UnifiedDispatch:Enabled", "true")
            }));

        builder.ConfigureServices(services =>
        {
            services.Configure<HostOptions>(o => o.ShutdownTimeout = TimeSpan.FromSeconds(5));
            services.RemoveAll<IHostedService>();

            RemoveDbContextRegistrations(services);
            services.AddSingleton<IDbContextFactory<PipelineDbContext>>(
                new UnifiedDispatchDbContextFactory(_dbName));

            services.RemoveAll<IDistributedLockProvider>();
            services.AddDistributedLockProvider(null);

            services.RemoveAll<IDatabaseProbe>();
            services.AddSingleton<IDatabaseProbe>(new NoOpDatabaseProbe());

            services.RemoveAll<IProviderFactory>();
            services.AddSingleton(new Mock<IProviderFactory>().Object);

            services.RemoveAll<IQualityGateValidator>();
            services.AddSingleton(new Mock<IQualityGateValidator>().Object);

            // IConsolidationDispatchService was removed in issue #2325 — no stub needed.

            services.RemoveAll<ILeaderElectionService>();
            var leaderMock = new Mock<ILeaderElectionService>();
            leaderMock.SetupGet(l => l.IsLeader).Returns(true);
            leaderMock.SetupGet(l => l.LeaderToken).Returns(CancellationToken.None);
            services.AddSingleton(leaderMock.Object);

            services.RemoveAll<AssignmentEnricher>();
            services.AddSingleton<AssignmentEnricher>(new PassthroughAssignmentEnricher());

            services.RemoveAll<IKubernetesJobClient>();
            services.AddSingleton(new Mock<IKubernetesJobClient>().Object);
        });
    }

    private static void RemoveDbContextRegistrations(IServiceCollection services)
    {
        var toRemove = services
            .Where(d => d.ServiceType == typeof(IDbContextFactory<PipelineDbContext>)
                     || d.ServiceType == typeof(PipelineDbContext)
                     || d.ServiceType == typeof(DbContextOptions<PipelineDbContext>)
                     || d.ServiceType == typeof(DbContextOptions)
                     || d.ServiceType.Name.Contains("DbContextPool"))
            .ToList();
        foreach (var d in toRemove) services.Remove(d);
    }

    // ── Test Infrastructure ──────────────────────────────────────────────────────

    private sealed class UnifiedDispatchDbContextFactory : IDbContextFactory<PipelineDbContext>
    {
        private readonly string _dbName;
        public UnifiedDispatchDbContextFactory(string dbName) => _dbName = dbName;

        public PipelineDbContext CreateDbContext()
        {
            var options = new DbContextOptionsBuilder<PipelineDbContext>()
                .UseInMemoryDatabase(_dbName)
                .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
                .Options;
            return new UnifiedDispatchTestPipelineDbContext(options);
        }

        public Task<PipelineDbContext> CreateDbContextAsync(CancellationToken ct = default)
            => Task.FromResult(CreateDbContext());
    }

    private sealed class UnifiedDispatchTestPipelineDbContext : PipelineDbContext
    {
        public UnifiedDispatchTestPipelineDbContext(DbContextOptions<PipelineDbContext> options) : base(options) { }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);

            // Remove RowVersion concurrency token — not supported by InMemory provider
            foreach (var entityType in modelBuilder.Model.GetEntityTypes())
            {
                var rowVersionProp = entityType.FindProperty("RowVersion");
                if (rowVersionProp != null)
                {
                    rowVersionProp.IsConcurrencyToken = false;
                    rowVersionProp.ValueGenerated = Microsoft.EntityFrameworkCore.Metadata.ValueGenerated.Never;
                }
            }

            // Remove filtered unique indexes — not supported by InMemory provider
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

    private sealed class NoOpDatabaseProbe : IDatabaseProbe
    {
        public Task ProbeAsync(CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class PassthroughAssignmentEnricher : AssignmentEnricher
    {
        public PassthroughAssignmentEnricher() : base(Serilog.Log.Logger) { }

        public override Task<JobDistributionRequest?> EnrichAsync(
            JobDistributionRequest identity, PipelineProject project, CancellationToken ct)
            => Task.FromResult<JobDistributionRequest?>(identity);
    }
}
