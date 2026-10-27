using System.Text.Json;
using CodingAgent.Api;
using CodingAgent.Infrastructure;
using CodingAgent.Infrastructure.Locking;
using CodingAgent.Infrastructure.Persistence;
using CodingAgent.Infrastructure.Persistence.Entities;
using CodingAgent.Infrastructure.Persistence.Services;
using CodingAgent.Kubernetes;
using CodingAgent.Pipeline;
using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.LeaderElection;
using CodingAgent.Pipeline.Models;
using CodingAgent.Web.TestUtilities;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Moq;
using Serilog;

namespace CodingAgent.Api.IntegrationTests;

/// <summary>
/// WebApplicationFactory targeting CodingAgent.Api.Program.
/// Uses InMemory EF Core instead of real PostgreSQL.
/// Database__SkipStartupInit and AGENT_API_KEY are set BEFORE the host builds.
///
/// Note: The concurrent-claim real-Postgres test (Req 4.5c) is deferred to
/// CodingAgent.Infrastructure.IntegrationTests — EF InMemory cannot exercise
/// xmin row-version tokens required for real concurrency testing.
/// </summary>
public class ApiWebApplicationFactory : WebApplicationFactory<Program>
{
    /// <summary>
    /// The API key used in all tests. Set as AGENT_API_KEY before host build.
    /// </summary>
    public const string ApiKey = "test-api-key";

    /// <summary>
    /// The user-token signing key used in all tests. Set as UserToken__SigningKey before host build;
    /// <see cref="TestUserTokens"/> signs with it.
    /// </summary>
    public const string UserTokenSigningKey = "test-user-token-signing-key-0123456789abcdef";

    private readonly string _dbName = $"ApiIntegration-{Guid.NewGuid():N}";

    /// <summary>The Id of the agent profile <see cref="SeedDefaultRoute"/> stores; profile Ids are GUIDs in the database.</summary>
    public const string DefaultProfileId = "0e5e0000-0000-4000-8000-000000000053";

    /// <summary>
    /// Spec 053 task 4.0: stores an agent profile on <see cref="AgentRoutingTestData.DefaultJobTemplate"/> and a global
    /// default route to it, so the API routes the work items a test creates. Idempotent. Test classes that create work
    /// items call it in their constructor, because a config import in another test replaces the routing.
    /// </summary>
    public void SeedDefaultRoute()
    {
        using var db = CreateDbContext();
        var profileId = Guid.Parse(DefaultProfileId);
        var profile = db.AgentProfiles.FirstOrDefault(p => p.Id == profileId);
        if (profile is null)
            db.AgentProfiles.Add(profile = new AgentProfileEntity { Id = profileId, Name = "Test agent" });
        profile.Configuration = JsonSerializer.Serialize(
            AgentRoutingTestData.Profile(DefaultProfileId, displayName: "Test agent"), PipelineJsonOptions.Default);
        var routing = db.KeyValueStore.FirstOrDefault(kv => kv.Key == CodingAgent.Pipeline.Models.AgentRouting.StorageKey);
        if (routing is null)
            db.KeyValueStore.Add(routing = new KeyValueEntity { Key = CodingAgent.Pipeline.Models.AgentRouting.StorageKey });
        routing.Value = AgentRoutingStore.Serialize(AgentRoutingTestData.Routing(DefaultProfileId));
        db.SaveChanges();
    }

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
            Environment.SetEnvironmentVariable("UserToken__SigningKey", null);
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
        Environment.SetEnvironmentVariable("UserToken__SigningKey", UserTokenSigningKey);

        builder.UseEnvironment("Development");

        // Enable build-time DI graph validation so any mis-ordered sub-method call in
        // AddApiOrchestration (e.g. AddTokenVending after AddLifecycleAndConsolidation) surfaces
        // as a host-startup failure in CI rather than a silent first-use exception in production.
        // ValidateScopes is set explicitly because UseDefaultServiceProvider replaces ASP.NET Core's
        // Development-environment default that would otherwise enable it automatically.
        builder.UseDefaultServiceProvider(options =>
        {
            options.ValidateOnBuild = true;
            options.ValidateScopes = true;
        });

        builder.ConfigureServices(services =>
        {
            // Reduce shutdown timeout in tests
            services.Configure<HostOptions>(o => o.ShutdownTimeout = TimeSpan.FromSeconds(5));

            // Override the Serilog.ILogger DI registration to prevent the ReloadableLogger.Freeze()
            // race condition when multiple WebApplicationFactory instances start concurrently.
            // The old approach (Log.Logger = CreateBootstrapLogger() in ConfigureWebHost) had a race:
            // two concurrent Program.Main calls both write to Log.Logger, and whichever factory's
            // UseSerilog lambda runs second finds Log.Logger already frozen → exception.
            // By replacing the ILogger registration here (which runs after Program.Main's
            // UseSerilog registration), we bypass the Freeze() code path entirely.
            // See also AgentHubGateKestrelFactory and HubAndDiTests which apply the same fix.
            services.RemoveAll<Serilog.ILogger>();
            services.AddSingleton<Serilog.ILogger>(
                new Serilog.LoggerConfiguration().MinimumLevel.Warning().CreateLogger());

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

            // IConsolidationDispatchService was removed in issue #2325 — no stub needed.

            // Replace ILeaderElectionService with a mock that is always the leader.
            // The real implementation needs K8s Lease — unavailable in test env.
            // DatabaseMaintenanceService and ApiSchedulerEndpoints gate on IsLeader.
            services.RemoveAll<ILeaderElectionService>();
            var leaderMock = new Mock<ILeaderElectionService>();
            leaderMock.SetupGet(l => l.IsLeader).Returns(true);
            leaderMock.SetupGet(l => l.LeaderToken).Returns(CancellationToken.None);
            services.AddSingleton(leaderMock.Object);

            // Replace AssignmentEnricher with a passthrough stub so integration tests that
            // exercise the /assignment endpoint (which now propagates enrichment failures as 503)
            // don't fail due to missing real dependencies (IProviderFactory, IAgentProfileStore, etc.).
            // The passthrough stub returns the identity request as-is — sufficient for endpoint routing tests.
            services.RemoveAll<AssignmentEnricher>();
            services.AddSingleton<AssignmentEnricher>(new PassthroughAssignmentEnricher());

            // Register a no-op IKubernetesJobClient so DispatchLifecycleService (required by
            // POST /api/work-items/{id}/dispatch) can be constructed at startup. The integration tests
            // do not call the dispatch endpoint so the no-op implementation is never invoked.
            // TODO: This mock means ValidateOnBuild = true cannot detect the accidental removal of
            // AddKubernetes(services) from AddApiOrchestration: the mock satisfies the IKubernetesJobClient
            // dependency even if the production registration is gone. The AddTokenVending →
            // AddLifecycleAndConsolidation ordering constraint is covered (neither ITokenVendingService
            // nor IConsolidationJobPreparationService are mocked). The AddKubernetes ordering constraint
            // is enforced only by the ordering comment in AddApiOrchestration.
            services.RemoveAll<IKubernetesJobClient>();
            services.AddSingleton(new Mock<IKubernetesJobClient>().Object);

            // Spec 053 task 4.0: job templates with a tool, as the Helm values define them. The default route
            // (SeedDefaultRoute) names a profile on the first one.
            services.RemoveAll<JobTemplateStore>();
            services.AddSingleton(AgentRoutingTestData.JobTemplates());

            ConfigureTestServices(services);
        });
    }

    /// <summary>Last-word service overrides for factories derived from this one.</summary>
    protected virtual void ConfigureTestServices(IServiceCollection services)
    {
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

    /// <summary>
    /// Passthrough AssignmentEnricher for integration tests. Returns the identity request as-is, with the agent config Id
    /// the real enricher always sets (spec 053: the claim builds the agent config from the route).
    /// Replaces the real <see cref="AssignmentEnricher"/> which depends on DispatchInfrastructure,
    /// IProviderFactory, and IAgentProfileStore — none of which are available in the test environment.
    /// </summary>
    private sealed class PassthroughAssignmentEnricher : AssignmentEnricher
    {
        public PassthroughAssignmentEnricher() : base(Serilog.Log.Logger) { }

        public override Task<EnrichmentResult> EnrichAsync(
            JobDistributionRequest identity, PipelineProject project, CancellationToken ct)
            => Task.FromResult<EnrichmentResult>(new EnrichmentResult.Enriched(identity with
            {
                AgentProviderConfigId = identity.AgentProviderConfigId ?? identity.ResolvedProfileId ?? DefaultProfileId,
            }));
    }
}
