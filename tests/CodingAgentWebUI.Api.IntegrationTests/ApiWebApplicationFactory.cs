using CodingAgentWebUI.Api;
using CodingAgentWebUI.Infrastructure;
using CodingAgentWebUI.Infrastructure.Locking;
using CodingAgentWebUI.Infrastructure.Persistence;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.LeaderElection;
using CodingAgentWebUI.Pipeline.Models;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Moq;
using Serilog;

namespace CodingAgentWebUI.Api.IntegrationTests;

/// <summary>
/// WebApplicationFactory targeting CodingAgentWebUI.Api.Program.
/// Uses InMemory EF Core instead of real PostgreSQL.
/// Database__SkipStartupInit and AGENT_API_KEY are set BEFORE the host builds.
///
/// Note: The concurrent-claim real-Postgres test (Req 4.5c) is deferred to
/// CodingAgentWebUI.Infrastructure.IntegrationTests — EF InMemory cannot exercise
/// xmin row-version tokens required for real concurrency testing.
/// </summary>
public sealed class ApiWebApplicationFactory : WebApplicationFactory<Program>
{
    /// <summary>
    /// The API key used in all tests. Set as AGENT_API_KEY before host build.
    /// </summary>
    public const string ApiKey = "test-api-key";

    private readonly string _dbName = $"ApiIntegration-{Guid.NewGuid():N}";

    /// <summary>
    /// Creates a fresh InMemory DbContext for seeding test data directly.
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
            Environment.SetEnvironmentVariable("Database__Host", null);
            Environment.SetEnvironmentVariable("Database__Port", null);
            Environment.SetEnvironmentVariable("Database__Username", null);
            Environment.SetEnvironmentVariable("Database__Password", null);
            Environment.SetEnvironmentVariable("Database__Name", null);
            Environment.SetEnvironmentVariable("Database__SslMode", null);
            Environment.SetEnvironmentVariable("Database__MigrateOnStartup", null);
            Environment.SetEnvironmentVariable("Database__SkipStartupInit", null);
            Environment.SetEnvironmentVariable("AGENT_API_KEY", null);
        }

        base.Dispose(disposing);
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        // Set environment variables BEFORE host builds — Program.cs fast-fail checks read these
        // during configuration build, before ConfigureServices runs.
        Environment.SetEnvironmentVariable("Database__Host", "localhost");
        Environment.SetEnvironmentVariable("Database__Port", "5432");
        Environment.SetEnvironmentVariable("Database__Username", "test");
        Environment.SetEnvironmentVariable("Database__Password", "test");
        Environment.SetEnvironmentVariable("Database__Name", "test_db");
        Environment.SetEnvironmentVariable("Database__SslMode", "Disable");
        Environment.SetEnvironmentVariable("Database__MigrateOnStartup", "false");
        Environment.SetEnvironmentVariable("Database__SkipStartupInit", "true");
        Environment.SetEnvironmentVariable("AGENT_API_KEY", ApiKey);

        // Reset Serilog's global logger to a fresh bootstrap state.
        // Use CreateBootstrapLogger() (not CreateLogger()) — UseSerilog captures Log.Logger
        // during service resolution and calls Freeze() on it if it's a ReloadableLogger.
        // CreateBootstrapLogger creates a fresh ReloadableLogger; CreateLogger() creates a
        // static logger that the Serilog hosting library handles differently and may cause
        // "logger is already frozen" when multiple factories run in the same test process.
        Serilog.Log.Logger = new Serilog.LoggerConfiguration()
            .MinimumLevel.Warning()
            .CreateBootstrapLogger();

        builder.UseEnvironment("Development");

        builder.ConfigureServices(services =>
        {
            // Reduce shutdown timeout in tests
            services.Configure<HostOptions>(o => o.ShutdownTimeout = TimeSpan.FromSeconds(5));

            // Remove all hosted services to prevent background loops in tests
            services.RemoveAll<IHostedService>();

            // Remove the real Npgsql DbContext registrations and replace with InMemory
            RemoveDbContextRegistrations(services);
            services.AddSingleton<IDbContextFactory<PipelineDbContext>>(
                new InMemoryDbContextFactory(_dbName));

            // Replace the distributed lock provider with InProcess (real one uses Postgres advisory locks)
            services.RemoveAll<IDistributedLockProvider>();
            services.AddDistributedLockProvider(null);

            // Replace IDatabaseProbe with a no-op so startup skips real SQL connectivity check
            services.RemoveAll<IDatabaseProbe>();
            services.AddSingleton<IDatabaseProbe>(new NoOpDatabaseProbe());

            // Replace IProviderFactory with a mock (no real GitHub/Kiro CLI in tests)
            services.RemoveAll<IProviderFactory>();
            services.AddSingleton(new Mock<IProviderFactory>().Object);

            // Replace IQualityGateValidator with a mock
            services.RemoveAll<IQualityGateValidator>();
            services.AddSingleton(new Mock<IQualityGateValidator>().Object);

            // Replace IConsolidationDispatchService with a no-op stub (production registration
            // requires Kubernetes/hub infra that's not available in tests)
            services.RemoveAll<IConsolidationDispatchService>();
            services.AddSingleton<IConsolidationDispatchService>(new NoOpConsolidationDispatchService());

            // Replace ILeaderElectionService with a mock that is always the leader.
            // The real implementation needs K8s Lease — unavailable in test env.
            // DatabaseMaintenanceService and ApiSchedulerEndpoints gate on IsLeader.
            services.RemoveAll<ILeaderElectionService>();
            var leaderMock = new Mock<ILeaderElectionService>();
            leaderMock.SetupGet(l => l.IsLeader).Returns(true);
            leaderMock.SetupGet(l => l.LeaderToken).Returns(CancellationToken.None);
            services.AddSingleton(leaderMock.Object);

            // Replace AssignmentEnricher with a stub that returns a minimal enriched result.
            // The real implementation requires live provider infrastructure (IProviderFactory,
            // IAgentProfileStore, DispatchInfrastructure) that is not available in tests.
            // Tests that validate enrichment failure → 503 use GetAssignmentTests (direct
            // method calls with FakeAssignmentEnricher) and do not go through this factory.
            services.RemoveAll<AssignmentEnricher>();
            services.AddSingleton<AssignmentEnricher>(new StubAssignmentEnricher());
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

        public Task<PipelineDbContext> CreateDbContextAsync(CancellationToken ct = default)
            => Task.FromResult(CreateDbContext());
    }

    /// <summary>
    /// PipelineDbContext subclass that disables Postgres-specific features
    /// (RowVersion concurrency tokens, filtered indexes) for InMemory compatibility.
    ///
    /// Note: this is a private sealed copy — TestPipelineDbContext is NOT a shared type.
    /// It appears in ~23 test files, each overriding OnModelCreating the same way.
    /// Pattern copied from DbModeWebApplicationFactory.cs:176.
    /// </summary>
    private sealed class TestPipelineDbContext : PipelineDbContext
    {
        public TestPipelineDbContext(DbContextOptions<PipelineDbContext> options) : base(options) { }

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

    private sealed class NoOpConsolidationDispatchService : IConsolidationDispatchService
    {
        public Task<ConsolidationDispatchResult> TryDispatchAsync(ConsolidationRun r, ConsolidationRunType t,
            TemplateId? tid, string? f, string w, CancellationToken ct)
            => Task.FromResult(ConsolidationDispatchResult.Failed);
        public Task<bool> TryDispatchToAgentAsync(RunId r, ConsolidationRunType t, TemplateId? tid,
            string w, AgentId a, CancellationToken ct)
            => Task.FromResult(false);
        public Task NotifyRunCancelledAsync(RunId r, CancellationToken ct) => Task.CompletedTask;
    }

    /// <summary>
    /// Test stub for <see cref="AssignmentEnricher"/> that returns a minimal enriched result
    /// for every request. Used in HTTP-level integration tests where real provider infrastructure
    /// (IProviderFactory, IAgentProfileStore, DispatchInfrastructure) is not available.
    /// Returns the identity request with empty-but-non-null mutable config collections so
    /// callers receive a valid 200 rather than a 503.
    /// </summary>
    private sealed class StubAssignmentEnricher : AssignmentEnricher
    {
        // TODO: [WARNING] Serilog.Log.Logger is the global static logger. If Serilog has not been
        // configured at the time this constructor runs (e.g., in some test runners that build the
        // factory before the test host is started), the base class receives the no-op logger.
        // This is safe as long as EnrichAsync is fully overridden (which it is), but any future
        // change that calls base.EnrichAsync from the override would silently swallow log output
        // or hit a NullReferenceException if the base class guards are relaxed. Prefer passing
        // a NullLogger (Serilog.Core.Logger.None) or a test-scoped logger instead.
        public StubAssignmentEnricher() : base(Serilog.Log.Logger) { }

        public override Task<JobDistributionRequest?> EnrichAsync(
            JobDistributionRequest identity,
            Pipeline.Models.PipelineProject project,
            CancellationToken ct)
        {
            // Return identity with minimal mutable config populated so GetAssignment returns 200.
            var enriched = identity with
            {
                ProviderConfigs = [],
                QualityGateConfigs = [],
                ReviewerConfigs = [],
                McpServers = [],
                PipelineConfiguration = new Pipeline.Models.PipelineConfiguration()
            };
            return Task.FromResult<JobDistributionRequest?>(enriched);
        }
    }
}
