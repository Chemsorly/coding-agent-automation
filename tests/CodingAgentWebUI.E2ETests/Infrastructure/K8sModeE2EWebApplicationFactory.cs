using CodingAgentWebUI.E2ETests.Fakes;
using CodingAgentWebUI.Infrastructure.Locking;
using CodingAgentWebUI.Infrastructure.Persistence;
using CodingAgentWebUI.Orchestration.Dispatch;
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
using Microsoft.Extensions.Logging;

namespace CodingAgentWebUI.E2ETests.Infrastructure;

/// <summary>
/// WebApplicationFactory for K8s-mode E2E tests. Boots the app with:
/// - Database env vars set to Kubernetes mode
/// - InMemory EF Core (replaces real Npgsql)
/// - FakeKubernetesJobClient (replaces real K8s API calls)
/// - FakeLeaderElectionService (always elected)
/// - Real DispatchService (polls Pending WorkItems, creates K8s Jobs via fake)
/// - Real KubernetesWorkDistributor (inserts WorkItem rows)
/// - Fake external providers (IProviderFactory, IConfigurationStore)
/// - NO SignalR agents (K8s mode uses pod-based agents, not SignalR)
/// - NO HeartbeatMonitorService (K8s mode uses ReconciliationService)
/// </summary>
public sealed class K8sModeE2EWebApplicationFactory : WebApplicationFactory<Program>
{
    public const string TestApiKey = "k8s-e2e-test-key";

    private readonly string _dbName = $"K8sModeE2E-{Guid.NewGuid()}";

    // Shared fake instances for seeding and assertions
    public InMemoryConfigurationStore ConfigStore { get; } = new();
    public FakeProviderFactory FakeProviders { get; } = new();
    public ConfigurableQualityGateValidator QualityGateValidator { get; } = new();
    public InMemoryPipelineRunHistoryService HistoryService { get; } = new();
    public FakeKubernetesJobClient FakeK8sClient { get; } = new();

    /// <summary>The base address of the running Kestrel server.</summary>
    public string ServerAddress => ClientOptions.BaseAddress.ToString().TrimEnd('/');

    /// <summary>InMemory DbContextFactory for test assertions.</summary>
    public IDbContextFactory<PipelineDbContext> DbContextFactory =>
        Services.GetRequiredService<IDbContextFactory<PipelineDbContext>>();

    public K8sModeE2EWebApplicationFactory()
    {
        UseKestrel(0);
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
            Environment.SetEnvironmentVariable("WorkDistribution__Mode", null);
            Environment.SetEnvironmentVariable("AGENT_API_KEY", null);
        }
        base.Dispose(disposing);
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        // Env vars to trigger DB mode (NOT Kubernetes — we manually register K8s services)
        // Using SignalR mode to avoid InClusterConfig() failure, then replacing services
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

            // ── Replace IWorkDistributor with KubernetesWorkDistributor ────────
            // (SignalR mode registers SignalRWorkDistributor — replace with K8s version)
            services.RemoveAll<IWorkDistributor>();
            services.AddSingleton<IWorkDistributor>(sp => new KubernetesWorkDistributor(
                sp.GetRequiredService<IDbContextFactory<PipelineDbContext>>(),
                sp.GetRequiredService<CodingAgentWebUI.Infrastructure.Persistence.Services.WorkItemTransitionService>(),
                sp.GetRequiredService<Microsoft.Extensions.Logging.ILoggerFactory>()
                    .CreateLogger<KubernetesWorkDistributor>()));

            // ── Replace IKubernetesJobClient with fake ────────────────────────
            services.RemoveAll<IKubernetesJobClient>();
            services.AddSingleton<IKubernetesJobClient>(FakeK8sClient);

            // ── Disable PipelineLoopService ───────────────────────────────────
            RemoveHostedService<PipelineLoopService>(services);

            // ── Disable PendingWorkItemDrainService (K8s uses DispatchService instead) ──
            RemoveHostedService<PendingWorkItemDrainService>(services);

            // ── Reduce shutdown timeout ───────────────────────────────────────
            services.Configure<HostOptions>(o => o.ShutdownTimeout = TimeSpan.FromSeconds(5));
        });
    }

    /// <summary>Resets all fakes and clears the InMemory database.</summary>
    public void ResetAll()
    {
        ConfigStore.Reset();
        ConfigStore.SeedDefaults();
        FakeProviders.Reset();
        QualityGateValidator.Reset();
        HistoryService.Reset();
        FakeK8sClient.Reset();

        using var db = DbContextFactory.CreateDbContext();
        db.WorkItems.RemoveRange(db.WorkItems);
        db.SaveChanges();
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
            (d.ImplementationType == typeof(T) ||
             d.ImplementationFactory?.Method.ReturnType == typeof(T))).ToList();
        foreach (var d in descriptors) services.Remove(d);

        // NOTE: Do NOT remove typed singleton registrations (d.ServiceType == typeof(T)).
        // Other services (e.g., IPipelineLoopService → PipelineLoopService forwarding,
        // LoopStatePersistenceService → IPipelineLoopService) depend on the singleton
        // remaining in DI. Removing only the IHostedService entry prevents the background
        // loop from executing without breaking the DI graph.
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

    private sealed class TestPipelineDbContext : PipelineDbContext
    {
        public TestPipelineDbContext(DbContextOptions<PipelineDbContext> options) : base(options) { }
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);
            // Remove xmin concurrency token (InMemory doesn't support it)
            modelBuilder.Entity<CodingAgentWebUI.Infrastructure.Persistence.Entities.WorkItemEntity>()
                .Property(e => e.RowVersion).IsConcurrencyToken(false);
            // Remove filtered index (InMemory doesn't support HasFilter)
            foreach (var entityType in modelBuilder.Model.GetEntityTypes())
            {
                foreach (var index in entityType.GetIndexes().ToList())
                {
                    if (index.GetFilter() is not null)
                        index.SetFilter(null);
                }
            }
        }
    }

    private sealed class NoOpDatabaseProbe : IDatabaseProbe
    {
        public Task ProbeAsync(CancellationToken ct) => Task.CompletedTask;
    }
}
