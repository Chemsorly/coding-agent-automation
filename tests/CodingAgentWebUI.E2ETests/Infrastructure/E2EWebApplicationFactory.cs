using CodingAgentWebUI.Api.Client;
using CodingAgentWebUI.E2ETests.Fakes;
using CodingAgentWebUI.Hub;
using CodingAgentWebUI.Infrastructure;
using CodingAgentWebUI.Infrastructure.Locking;
using CodingAgentWebUI.Infrastructure.Persistence;
using CodingAgentWebUI.Kubernetes;
using CodingAgentWebUI.Orchestration;
using CodingAgentWebUI.Orchestration.Dispatch;
using CodingAgentWebUI.Orchestration.Registry;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Services;
using CodingAgentWebUI.Services;
using CodingAgentWebUI.TestUtilities;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace CodingAgentWebUI.E2ETests.Infrastructure;

/// <summary>
/// Starts the Blazor Server app on a real Kestrel port with every external dependency replaced
/// by an in-memory fake. Paired with <see cref="ApiE2EWebApplicationFactory"/> by
/// <see cref="E2EFixture"/>; the two share a database name, a configuration store and a run
/// history service, so a test seeds once and both processes see it.
///
/// This is the sole monolith factory. Until the 041–045 arc there were four —
/// <c>E2EWebApplicationFactory</c> (Legacy), <c>DbModeE2EWebApplicationFactory</c> (DB+SignalR),
/// <c>K8sModeE2EWebApplicationFactory</c> and <c>K8sChatE2EWebApplicationFactory</c> — one per
/// deployment mode. Spec 041 deleted every mode but Kubernetes+DB, which left four near-identical
/// copies of one topology whose only real differences were which fakes they happened to register.
/// They are merged here: everything any of them registered is registered once, and the K8s job
/// client and chat dispatcher are always available rather than only in the factory named for them.
/// </summary>
public sealed class E2EWebApplicationFactory : WebApplicationFactory<WebUiHostMarker>
{
    public const string TestApiKey = "e2e-test-key";

    private readonly string _dbName = $"E2E-{Guid.NewGuid()}";

    /// <summary>EF InMemory database name, shared with the API host so both see one WorkItem set.</summary>
    public string DbName => _dbName;

    /// <summary>
    /// Base URL of the Pipeline API host, set by the fixture before this host builds. Left null
    /// when the Blazor app runs alone, in which case an unroutable address is used instead — see
    /// <see cref="E2ETestDefaults.UnreachableApiBaseUrl"/>.
    /// </summary>
    public string? ApiBaseUrl { get; set; }

    // Shared fake instances — accessible by tests for seeding and assertions
    public Fakes.InMemoryConfigurationStore ConfigStore { get; } = new();
    public FakeProviderFactory FakeProviders { get; } = new();

    /// <summary>Pipeline API config client backed by <see cref="ConfigStore"/>.</summary>
    public InMemoryPipelineApiConfigClient ApiConfigClient => _apiConfigClient ??= new InMemoryPipelineApiConfigClient(ConfigStore);
    private InMemoryPipelineApiConfigClient? _apiConfigClient;
    public ConfigurableQualityGateValidator QualityGateValidator { get; } = new();
    public InMemoryPipelineRunHistoryService HistoryService { get; } = new();

    /// <summary>
    /// Stands in for the Kubernetes Job API. Chat pods and consolidation pods are dispatched
    /// through it, so tests assert on <see cref="FakeKubernetesJobClient.CreatedJobs"/> and
    /// <see cref="FakeKubernetesJobClient.ChatJobs"/> instead of against a cluster.
    /// </summary>
    public FakeKubernetesJobClient FakeK8sClient { get; } = new();

    // Resettable services — created during ConfigureServices, used in ResetAll
    private ResettablePipelineOrchestrationService? _orchestration;
    private AgentRegistryService? _registry;
    private OrchestratorRunService? _runService;
    private JobDeduplicationGuardService? _dispatcher;

    /// <summary>Exposes the agent registry for test assertions and wait helpers.</summary>
    public AgentRegistryService AgentRegistry => _registry ?? throw new InvalidOperationException("Not initialized");

    /// <summary>The real <see cref="ChatJobDispatcher"/>, backed by <see cref="FakeK8sClient"/>.</summary>
    public ChatJobDispatcher ChatDispatcher => Services.GetRequiredService<ChatJobDispatcher>();

    /// <summary>InMemory DbContextFactory, for test assertions against WorkItem state.</summary>
    public IDbContextFactory<PipelineDbContext> DbContextFactory =>
        Services.GetRequiredService<IDbContextFactory<PipelineDbContext>>();

    public E2EWebApplicationFactory()
    {
        // Use the .NET 10 first-class API to start real Kestrel on a random port
        UseKestrel(0);
    }

    /// <summary>The base address of the running Kestrel server (e.g., http://localhost:12345).</summary>
    public string ServerAddress => ClientOptions.BaseAddress.ToString().TrimEnd('/');

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            // Clean up process-global env vars so subsequent test factories
            // (which run serially due to DisableTestParallelization) start clean.
            E2ETestDefaults.ClearDatabaseEnvironment();
        }

        base.Dispose(disposing);
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        // Set the API key via environment variable before host builds
        Environment.SetEnvironmentVariable("AGENT_API_KEY", TestApiKey);

        E2ETestDefaults.ApplyDatabaseEnvironment();

        // Spec 045: the monolith fast-fails at startup without a Pipeline API base URL.
        Environment.SetEnvironmentVariable(
            "PipelineApi__BaseUrl", ApiBaseUrl ?? E2ETestDefaults.UnreachableApiBaseUrl);

        // No config caching: the harness seeds through the store between assertions and a stale
        // read would make tests depend on wall-clock timing.
        Environment.SetEnvironmentVariable("PipelineLoop__ConfigCacheTtlSeconds", "0");

        E2ETestDefaults.ResetSerilogBootstrapLogger();

        // Set environment to Development so static web assets are resolved correctly.
        builder.UseEnvironment("Development");

        builder.ConfigureServices(services =>
        {
            // Seed default test data
            ConfigStore.SeedDefaults();

            // Replace the Npgsql context with EF InMemory. The stores below are all faked, but
            // the monolith still resolves IDbContextFactory<PipelineDbContext> at startup for
            // KubernetesWorkDistributor / KubernetesJobCleanup and would open a real connection.
            E2EInMemoryDatabase.Install(services, _dbName);

            // Replace external provider interfaces with fakes
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

            // JobTemplateStore loads /app/config/job-templates.yaml, which only exists in-cluster
            // (mounted from the job-templates ConfigMap). ChatJobDispatcher depends on it.
            services.RemoveAll<JobTemplateStore>();
            services.AddSingleton(JobTemplateStore.LoadFromYaml("""
                - labels: "kiro,dotnet"
                  image: "chemsorly/coding-agent:kiro-dotnet10-latest"
                  providerType: "kiro"
                  maxConcurrent: 1
                """));
            ReplaceService<IProviderFactory>(services, FakeProviders);
            ReplaceService<IQualityGateValidator>(services, QualityGateValidator);
            ReplaceService<IPipelineRunHistoryService>(services, HistoryService);

            // Every Job the app would create in a cluster lands in the fake instead. This is the
            // only K8s seam the monolith still owns — IWorkDistributor is already
            // KubernetesWorkDistributor for everyone since Spec 041, so it needs no replacement.
            services.RemoveAll<IKubernetesJobClient>();
            services.AddSingleton<IKubernetesJobClient>(FakeK8sClient);

            // Postgres advisory locks become in-process locks.
            services.RemoveAll<IDistributedLockProvider>();
            services.AddDistributedLockProvider(null);

            // Report the database healthy without probing it, and skip the startup probe entirely.
            services.RemoveAll<DatabaseHealthState>();
            services.AddSingleton(new DatabaseHealthState());
            services.AddSingleton<IDatabaseProbe>(new NoOpDatabaseProbe());

            // Replace singleton services with resettable subclasses
            ReplaceWithResettableServices(services);

            // Disable PipelineLoopService (background polling)
            RemoveHostedService<PipelineLoopService>(services);

            // Reduce shutdown timeout for faster test teardown
            services.Configure<HostOptions>(o => o.ShutdownTimeout = TimeSpan.FromSeconds(5));
        });
    }

    private void ReplaceWithResettableServices(IServiceCollection services)
    {
        // AgentRegistryService — sealed, uses internal Reset()
        _registry = new AgentRegistryService(Serilog.Log.Logger);
        RemoveService<AgentRegistryService>(services);
        services.AddSingleton(_registry);

        // OrchestratorRunService — sealed, uses internal Reset()
        _runService = new OrchestratorRunService(Serilog.Log.Logger);
        RemoveService<OrchestratorRunService>(services);
        RemoveService<IOrchestratorRunService>(services);
        services.AddSingleton(_runService);
        services.AddSingleton<IOrchestratorRunService>(_runService);

        // JobDeduplicationGuardService — sealed, uses internal Reset()
        _dispatcher = new JobDeduplicationGuardService(_registry, Serilog.Log.Logger);
        RemoveService<JobDeduplicationGuardService>(services);
        services.AddSingleton(_dispatcher);

        // PipelineOrchestrationService → ResettablePipelineOrchestrationService
        var lifecycle = new PipelineRunLifecycleService(HistoryService, _runService, Serilog.Log.Logger);
        _orchestration = new ResettablePipelineOrchestrationService(
            ConfigStore,
            FakeProviders,
            new PipelineCancellationFacade(null, null),
            lifecycle,
            TestOrchestrationFactory.NoOpLabelService.Instance,
            Serilog.Log.Logger);
        RemoveService<PipelineOrchestrationService>(services);
        services.AddSingleton(_orchestration);
        services.AddSingleton<PipelineOrchestrationService>(_orchestration);
    }

    /// <summary>
    /// Resets all fakes and singleton services for test isolation.
    /// </summary>
    public void ResetAll()
    {
        ConfigStore.Reset();
        ConfigStore.SeedDefaults();
        FakeProviders.Reset();
        QualityGateValidator.Reset();
        HistoryService.Reset();
        FakeK8sClient.Reset();

        // Clear the InMemory database
        using (var db = DbContextFactory.CreateDbContext())
        {
            db.WorkItems.RemoveRange(db.WorkItems);
            db.SaveChanges();
        }

        // Reset resettable service subclasses
        _orchestration?.Reset();
        _registry?.Reset();
        _runService?.Reset();
        _dispatcher?.Reset();

        // Reset consolidation badge service
        var badgeService = Services.GetRequiredService<ConsolidationBadgeService>();
        badgeService.Reset();

        // Reset consolidation service in-memory concurrency state
        var consolidationService = Services.GetRequiredService<IConsolidationService>();
        if (consolidationService is ConsolidationService cs)
            cs.Reset();
    }

    private static void ReplaceService<T>(IServiceCollection services, T implementation) where T : class
    {
        RemoveService<T>(services);
        services.AddSingleton(implementation);
    }

    private static void RemoveService<T>(IServiceCollection services)
    {
        var descriptors = services.Where(d => d.ServiceType == typeof(T)).ToList();
        foreach (var descriptor in descriptors)
            services.Remove(descriptor);
    }

    /// <summary>
    /// Unhosts <typeparamref name="T"/> without unregistering it.
    ///
    /// Only the <see cref="IHostedService"/> descriptor is removed — the typed singleton stays,
    /// because other services resolve it directly (<c>IPipelineLoopService</c> forwards to
    /// <c>PipelineLoopService</c>, and <c>LoopStatePersistenceService</c> takes that forward).
    /// Removing the typed registration too breaks the DI graph at startup.
    /// </summary>
    private static void RemoveHostedService<T>(IServiceCollection services) where T : class
    {
        var descriptors = services.Where(d =>
            d.ServiceType == typeof(IHostedService) &&
            (d.ImplementationType == typeof(T) ||
             d.ImplementationFactory?.Method.ReturnType == typeof(T))).ToList();
        foreach (var descriptor in descriptors)
            services.Remove(descriptor);
    }

    private sealed class NoOpDatabaseProbe : IDatabaseProbe
    {
        public Task ProbeAsync(CancellationToken ct) => Task.CompletedTask;
    }
}
