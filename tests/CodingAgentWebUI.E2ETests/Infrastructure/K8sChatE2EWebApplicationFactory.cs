using CodingAgentWebUI.E2ETests.Fakes;
using CodingAgentWebUI.Hub;
using CodingAgentWebUI.Infrastructure;
using CodingAgentWebUI.Infrastructure.Locking;
using CodingAgentWebUI.Infrastructure.Persistence;
using CodingAgentWebUI.Infrastructure.Persistence.Services;
using CodingAgentWebUI.Kubernetes;
using CodingAgentWebUI.Api.Client;
using CodingAgentWebUI.Orchestration.Dispatch;
using CodingAgentWebUI.Pipeline.LeaderElection;
using CodingAgentWebUI.Orchestration.Registry;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Services;
using CodingAgentWebUI.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CodingAgentWebUI.E2ETests.Infrastructure;

/// <summary>
/// WebApplicationFactory for K8s chat E2E tests.
/// Combines K8s-mode wiring with real <see cref="ChatJobDispatcher"/> backed by
/// <see cref="FakeKubernetesJobClient"/>.
/// </summary>
public sealed class K8sChatE2EWebApplicationFactory : WebApplicationFactory<WebUiHostMarker>
{
    public const string TestApiKey = "k8s-chat-e2e-test-key";

    private readonly string _dbName = $"K8sChatE2E-{Guid.NewGuid()}";



    /// <summary>EF InMemory database name, shared with the API host in the two-service harness.</summary>


    public string DbName => _dbName;



    /// <summary>Base URL of the Pipeline API host, set by the fixture before this host builds.</summary>


    public string? ApiBaseUrl { get; set; }

    // Shared fakes
    public InMemoryConfigurationStore ConfigStore { get; } = new();
    public FakeProviderFactory FakeProviders { get; } = new();

    /// <summary>Pipeline API config client backed by <see cref="ConfigStore"/>.</summary>
    public InMemoryPipelineApiConfigClient ApiConfigClient => _apiConfigClient ??= new InMemoryPipelineApiConfigClient(ConfigStore);
    private InMemoryPipelineApiConfigClient? _apiConfigClient;
    public FakeKubernetesJobClient FakeK8sClient { get; } = new();

    /// <summary>The real <see cref="ChatJobDispatcher"/> instance. Access after InitializeAsync.</summary>
    public ChatJobDispatcher ChatDispatcher =>
        Services.GetRequiredService<ChatJobDispatcher>();

    public string ServerAddress => ClientOptions.BaseAddress.ToString().TrimEnd('/');

    public IDbContextFactory<PipelineDbContext> DbContextFactory =>
        Services.GetRequiredService<IDbContextFactory<PipelineDbContext>>();

    public K8sChatE2EWebApplicationFactory()
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
        // Spec 045: the monolith fast-fails at startup without a Pipeline API base URL.
        Environment.SetEnvironmentVariable(
            "PipelineApi__BaseUrl", ApiBaseUrl ?? E2ETestDefaults.UnreachableApiBaseUrl);

        builder.UseEnvironment("Development");

        builder.ConfigureServices(services =>
        {
            ConfigStore.SeedDefaults();

            // ── Replace external providers ─────────────────────────────────
            ReplaceService<IConfigurationStore>(services, ConfigStore);
            ReplaceService<IPipelineConfigStore>(services, ConfigStore);
            ReplaceService<IProviderConfigStore>(services, ConfigStore);
            ReplaceService<IAgentProfileStore>(services, ConfigStore);
            ReplaceService<IQualityGateConfigStore>(services, ConfigStore);
            ReplaceService<IReviewerConfigStore>(services, ConfigStore);
            ReplaceService<IProjectStore>(services, ConfigStore);

            // Spec 045: the UI reads and writes config through the Pipeline API client, not the
            // store. Back the client with the same in-memory store so seeding still reaches the UI
            // (and so startup does not retry against an unreachable API for ten minutes).
            ReplaceService<IPipelineApiConfigClient>(services, ApiConfigClient);

            // LeaderElectionService is a hosted service taking IKubernetes; without a stub the host
            // dies at startup wherever no kubeconfig exists.
            E2ETestDefaults.InstallKubernetesStub(services);
            ReplaceService<IProviderFactory>(services, FakeProviders);

            // ── InMemory DB ────────────────────────────────────────────────
            RemoveDbContextRegistrations(services);
            services.AddSingleton<IDbContextFactory<PipelineDbContext>>(
                new InMemoryDbContextFactory(_dbName));

            // ── Distributed lock ──────────────────────────────────────────
            services.RemoveAll<IDistributedLockProvider>();
            services.AddDistributedLockProvider(null);

            // ── DatabaseHealthState ───────────────────────────────────────
            services.RemoveAll<DatabaseHealthState>();
            services.AddSingleton(new DatabaseHealthState());
            services.AddSingleton<IDatabaseProbe>(new NoOpDatabaseProbe());

            // ── K8s work distributor ──────────────────────────────────────
            services.RemoveAll<IWorkDistributor>();
            services.AddSingleton<IWorkDistributor>(sp => new KubernetesWorkDistributor(
                sp.GetRequiredService<CodingAgentWebUI.Api.Client.IPipelineApiWorkItemClient>(),
                sp.GetRequiredService<IDbContextFactory<PipelineDbContext>>(),
                sp.GetRequiredService<WorkItemTransitionService>(),
                sp.GetRequiredService<Microsoft.Extensions.Logging.ILoggerFactory>()
                    .CreateLogger<KubernetesWorkDistributor>()));

            // ── Fake K8s job client ───────────────────────────────────────
            services.RemoveAll<IKubernetesJobClient>();
            services.AddSingleton<IKubernetesJobClient>(FakeK8sClient);

            // ── Disable unneeded hosted services ──────────────────────────
            RemoveHostedService<PipelineLoopService>(services);
            // PendingWorkItemDrainService was deleted in Spec 041 — no-op

            // ── JobTemplateStore with kiro,dotnet + kiro,python templates ─
            services.RemoveAll<JobTemplateStore>();
            services.AddSingleton<JobTemplateStore>(_ => JobTemplateStore.LoadFromYaml("""
                - labels: "kiro,dotnet"
                  image: "chemsorly/coding-agent:kiro-dotnet10-latest"
                  imagePullPolicy: "Always"
                  providerType: "kiro"
                  maxConcurrent: 5
                - labels: "kiro,python"
                  image: "chemsorly/coding-agent:kiro-python-latest"
                  imagePullPolicy: "Always"
                  providerType: "kiro"
                  maxConcurrent: 5
                - labels: "kiro,node"
                  image: "chemsorly/coding-agent:kiro-node-latest"
                  imagePullPolicy: "Always"
                  providerType: "kiro"
                  maxConcurrent: 5
                - labels: "opencode,dotnet"
                  image: "chemsorly/coding-agent:opencode-dotnet10-latest"
                  imagePullPolicy: "Always"
                  providerType: "opencode"
                  maxConcurrent: 5
                """));

            // ── Always-leader stub (single test process, no competition) ─────
            services.RemoveAll<ILeaderElectionService>();
            services.AddSingleton<ILeaderElectionService>(new AlwaysLeaderElectionService());

            // ── ChatJobDispatcher (real, backed by fakes) ─────────────────
            var testOptions = new DispatchServiceOptions
            {
                Namespace = "test",
                OrchestratorUrl = "http://test-orchestrator",
                AgentApiKeySecretName = "agent-api-key",
                AgentServiceAccountName = "agent-sa",
                KiroPvcPool = new List<string> { "fake-pvc-0", "fake-pvc-1" },
                ChatSessionMaxDurationSeconds = 7200,
                ChatPodConnectTimeoutSeconds = 30,
                ChatTerminationGracePeriodSeconds = 10
            };

            services.AddSingleton<ChatJobDispatcher>(sp => new ChatJobDispatcher(
                FakeK8sClient,
                sp.GetRequiredService<IHubContext<AgentHub, IAgentHubClient>>(),
                sp.GetRequiredService<JobTemplateStore>(),
                sp.GetRequiredService<AgentRegistryService>(),
                testOptions,
                sp.GetRequiredService<ILeaderElectionService>(),
                Serilog.Log.Logger));

            services.AddHostedService(sp => sp.GetRequiredService<ChatJobDispatcher>());

            // Remove NullChatJobDispatcher (registered by SignalR mode) and replace
            services.RemoveAll<IChatJobDispatcher>();
            services.AddSingleton<IChatJobDispatcher>(sp => sp.GetRequiredService<ChatJobDispatcher>());

            // ── Shutdown timeout ──────────────────────────────────────────
            services.Configure<HostOptions>(o => o.ShutdownTimeout = TimeSpan.FromSeconds(5));
        });
    }

    /// <summary>Resets all fakes for test isolation.</summary>
    public void ResetAll()
    {
        ConfigStore.Reset();
        ConfigStore.SeedDefaults();
        FakeProviders.Reset();
        FakeK8sClient.Reset();

        using var db = DbContextFactory.CreateDbContext();
        db.WorkItems.RemoveRange(db.WorkItems);
        db.SaveChanges();
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

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
    }

    private static void RemoveDbContextRegistrations(IServiceCollection services)
    {
        var toRemove = services
            .Where(d =>
                d.ServiceType == typeof(IDbContextFactory<PipelineDbContext>) ||
                d.ServiceType == typeof(PipelineDbContext) ||
                d.ServiceType.Name.Contains("DbContextPool") ||
                d.ServiceType == typeof(DbContextOptions<PipelineDbContext>) ||
                d.ServiceType == typeof(DbContextOptions))
            .ToList();
        foreach (var d in toRemove) services.Remove(d);
    }

    // ── Inner types ───────────────────────────────────────────────────────────

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
            modelBuilder.Entity<CodingAgentWebUI.Infrastructure.Persistence.Entities.WorkItemEntity>()
                .Property(e => e.RowVersion).IsConcurrencyToken(false);
            foreach (var entityType in modelBuilder.Model.GetEntityTypes())
                foreach (var index in entityType.GetIndexes().ToList())
                    if (index.GetFilter() is not null)
                        index.SetFilter(null);
        }
    }

    private sealed class NoOpDatabaseProbe : IDatabaseProbe
    {
        public Task ProbeAsync(CancellationToken ct) => Task.CompletedTask;
    }

    // TODO: This private AlwaysLeaderElectionService is a duplicate of the production
    // CodingAgentWebUI.Orchestration.LeaderElection.AlwaysLeaderElectionService introduced
    // in the fix for issue #2009. Replace this private copy with a reference to the production
    // type to avoid divergence: if ILeaderElectionService gains a new member, this copy will
    // cause a compilation failure that requires a manual sync.
    private sealed class AlwaysLeaderElectionService : ILeaderElectionService
    {
        public bool IsLeader => true;
        public CancellationToken LeaderToken => CancellationToken.None;
#pragma warning disable CS0067
        public event Action? OnStartedLeading;
        public event Action? OnStoppedLeading;
#pragma warning restore CS0067
    }
}
