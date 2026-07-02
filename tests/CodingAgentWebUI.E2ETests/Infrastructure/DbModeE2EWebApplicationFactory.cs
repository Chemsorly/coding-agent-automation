using CodingAgentWebUI.E2ETests.Fakes;
using CodingAgentWebUI.Infrastructure.Locking;
using CodingAgentWebUI.Infrastructure.Persistence;
using CodingAgentWebUI.Orchestration;
using CodingAgentWebUI.Orchestration.Dispatch;
using CodingAgentWebUI.Orchestration.Registry;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Services;
using CodingAgentWebUI.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace CodingAgentWebUI.E2ETests.Infrastructure;

/// <summary>
/// WebApplicationFactory for DB-mode E2E tests. Boots the app with:
/// - Database env vars (triggers SignalR + DB code paths in Program.cs)
/// - InMemory EF Core (replaces real Npgsql)
/// - Real SignalRWorkDistributor, PendingWorkItemDrainService, DispatchOrchestrationService,
///   RunLifecycleManager, WorkItemTransitionService, HeartbeatMonitorService
/// - Fake external providers (IProviderFactory, IConfigurationStore, IQualityGateValidator)
/// - Real Kestrel on a random port for FakeAgentClient SignalR connections
/// </summary>
public sealed class DbModeE2EWebApplicationFactory : WebApplicationFactory<Program>
{
    public const string TestApiKey = "db-e2e-test-key";

    private readonly string _dbName = $"DbModeE2E-{Guid.NewGuid()}";

    // Shared fake instances for seeding and assertions
    public InMemoryConfigurationStore ConfigStore { get; } = new();
    public FakeProviderFactory FakeProviders { get; } = new();
    public ConfigurableQualityGateValidator QualityGateValidator { get; } = new();
    public InMemoryPipelineRunHistoryService HistoryService { get; } = new();

    public DbModeE2EWebApplicationFactory()
    {
        // Real Kestrel on random port for SignalR connections
        UseKestrel(0);
    }

    /// <summary>The base address of the running Kestrel server.</summary>
    public string ServerAddress => ClientOptions.BaseAddress.ToString().TrimEnd('/');

    /// <summary>
    /// Provides access to the InMemory DbContextFactory for test assertions against WorkItem state.
    /// </summary>
    public IDbContextFactory<PipelineDbContext> DbContextFactory =>
        Services.GetRequiredService<IDbContextFactory<PipelineDbContext>>();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            // Clean up process-global env vars
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
        // Set env vars BEFORE host builds — these trigger DB mode in Program.cs
        Environment.SetEnvironmentVariable("Database__Host", "localhost");
        Environment.SetEnvironmentVariable("Database__Port", "5432");
        Environment.SetEnvironmentVariable("Database__Username", "test");
        Environment.SetEnvironmentVariable("Database__Password", "test");
        Environment.SetEnvironmentVariable("Database__Name", "test_db");
        Environment.SetEnvironmentVariable("Database__SslMode", "Disable");
        Environment.SetEnvironmentVariable("Database__MigrateOnStartup", "false");
        Environment.SetEnvironmentVariable("Database__SkipStartupInit", "true");
        Environment.SetEnvironmentVariable("WorkDistribution__Mode", "SignalR");
        Environment.SetEnvironmentVariable("AGENT_API_KEY", TestApiKey);

        builder.UseEnvironment("Development");

        builder.ConfigureServices(services =>
        {
            // Seed default test data
            ConfigStore.SeedDefaults();

            // ── Replace external providers with fakes ──────────────────────────
            ReplaceService<IConfigurationStore>(services, ConfigStore);
            ReplaceService<IPipelineConfigStore>(services, ConfigStore);
            ReplaceService<IProviderConfigStore>(services, ConfigStore);
            ReplaceService<IAgentProfileStore>(services, ConfigStore);
            ReplaceService<IQualityGateConfigStore>(services, ConfigStore);
            ReplaceService<IReviewerConfigStore>(services, ConfigStore);
            ReplaceService<IProjectStore>(services, ConfigStore);
            ReplaceService<IProviderFactory>(services, FakeProviders);
            ReplaceService<IQualityGateValidator>(services, QualityGateValidator);
            ReplaceService<IPipelineRunHistoryService>(services, HistoryService);

            // ── Replace Npgsql DbContext with InMemory EF ─────────────────────
            RemoveDbContextRegistrations(services);
            services.AddSingleton<IDbContextFactory<PipelineDbContext>>(
                new InMemoryDbContextFactory(_dbName));

            // ── Replace distributed lock with InProcess ───────────────────────
            services.RemoveAll<IDistributedLockProvider>();
            services.AddDistributedLockProvider(null);

            // ── Replace DatabaseHealthState with pre-healthy ──────────────────
            services.RemoveAll<DatabaseHealthState>();
            services.AddSingleton(new DatabaseHealthState());

            // ── Register no-op IDatabaseProbe ─────────────────────────────────
            services.AddSingleton<IDatabaseProbe>(new NoOpDatabaseProbe());

            // ── Disable PipelineLoopService (no background polling) ───────────
            RemoveHostedService<PipelineLoopService>(services);

            // ── Keep PendingWorkItemDrainService running (needed for drain tests)
            // ── Keep HeartbeatMonitorService running (needed for disconnect tests)
            // These are registered by AddWorkDistribution / AddOrchestrationServices
            // and use the InMemory DB via the factory.

            // ── Reduce shutdown timeout for faster test teardown ──────────────
            services.Configure<HostOptions>(o => o.ShutdownTimeout = TimeSpan.FromSeconds(5));
        });
    }

    /// <summary>
    /// Resets all fakes and clears the InMemory database for test isolation.
    /// </summary>
    public void ResetAll()
    {
        ConfigStore.Reset();
        ConfigStore.SeedDefaults();
        FakeProviders.Reset();
        QualityGateValidator.Reset();
        HistoryService.Reset();

        // Clear the InMemory database
        using var db = DbContextFactory.CreateDbContext();
        db.WorkItems.RemoveRange(db.WorkItems);
        db.SaveChanges();

        // Reset agent registry
        var registry = Services.GetRequiredService<AgentRegistryService>();
        registry.Reset();

        // Reset run service
        var runService = Services.GetRequiredService<OrchestratorRunService>();
        runService.Reset();

        // Reset job dispatcher
        var dispatcher = Services.GetRequiredService<JobDispatcherService>();
        dispatcher.Reset();

        // Reset consolidation badge
        var badgeService = Services.GetRequiredService<ConsolidationBadgeService>();
        badgeService.Reset();

        // Reset consolidation service
        var consolidationService = Services.GetRequiredService<IConsolidationService>();
        if (consolidationService is ConsolidationService cs)
            cs.Reset();

        // Reset consolidation queue
        var queueService = Services.GetRequiredService<ConsolidationQueueService>();
        queueService.Reset();
    }

    // ── Helpers ───────────────────────────────────────────────────────────

    private static void ReplaceService<T>(IServiceCollection services, T implementation) where T : class
    {
        var descriptors = services.Where(d => d.ServiceType == typeof(T)).ToList();
        foreach (var d in descriptors) services.Remove(d);
        services.AddSingleton(implementation);
    }

    private static void RemoveHostedService<T>(IServiceCollection services) where T : class
    {
        var descriptors = services.Where(d =>
            d.ServiceType == typeof(IHostedService) &&
            d.ImplementationType == typeof(T)).ToList();
        foreach (var d in descriptors) services.Remove(d);
    }

    private static void RemoveDbContextRegistrations(IServiceCollection services)
    {
        var factoryDescriptors = services
            .Where(d => d.ServiceType == typeof(IDbContextFactory<PipelineDbContext>))
            .ToList();
        foreach (var d in factoryDescriptors) services.Remove(d);

        var scopedDescriptors = services
            .Where(d => d.ServiceType == typeof(PipelineDbContext))
            .ToList();
        foreach (var d in scopedDescriptors) services.Remove(d);

        var poolDescriptors = services
            .Where(d => d.ServiceType.Name.Contains("DbContextPool"))
            .ToList();
        foreach (var d in poolDescriptors) services.Remove(d);

        var optionsDescriptors = services
            .Where(d => d.ServiceType == typeof(DbContextOptions<PipelineDbContext>)
                     || d.ServiceType == typeof(DbContextOptions))
            .ToList();
        foreach (var d in optionsDescriptors) services.Remove(d);
    }

    // ── Inner classes ─────────────────────────────────────────────────────

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

            // Remove RowVersion concurrency token config (InMemory doesn't support xmin)
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

    private sealed class NoOpDatabaseProbe : IDatabaseProbe
    {
        public Task ProbeAsync(CancellationToken ct) => Task.CompletedTask;
    }
}
