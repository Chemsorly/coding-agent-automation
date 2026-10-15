using CodingAgent.Api.Client;
using CodingAgent.Web.E2ETests.Fakes;
using CodingAgent.AgentGateway;
using CodingAgent.Infrastructure;
using CodingAgent.Infrastructure.Locking;
using CodingAgent.Infrastructure.Persistence;
using CodingAgent.Kubernetes;
using CodingAgent.Orchestration;
using CodingAgent.Orchestration.Dispatch;
using CodingAgent.Orchestration.Registry;
using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;
using CodingAgent.Pipeline.Services;
using CodingAgent.Web.Services;
using CodingAgent.Web.TestUtilities;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace CodingAgent.Web.E2ETests.Infrastructure;

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

    /// <summary>Local admin password of the E2E web host (Spec 049).</summary>
    public const string TestAdminPassword = "e2e-admin-password";

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
    public InMemoryConfigurationStore ConfigStore { get; } = new(FakeProviderFactory.ExtraProviderConfigs);
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


    /// <summary>
    /// ChatJobDispatcher moved to the API host (Spec 044/045). Use <c>E2EFixture.ChatDispatcher</c>
    /// instead of this property — it now throws to prevent silent misuse.
    /// </summary>
    public ChatJobDispatcher ChatDispatcher => // NOSONAR CA1822 — property kept non-static; accessed via instance in E2EFixture
        throw new InvalidOperationException(
            "ChatJobDispatcher moved to the API host (Spec 044/045). Use Fixture.ChatDispatcher instead of Fixture.Factory.ChatDispatcher.");

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
            Environment.SetEnvironmentVariable("Auth__Admin__Password", null);
            E2ETestSignIn.ClearBindings();
        }

        base.Dispose(disposing);
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        // Set the API key via environment variable before host builds
        Environment.SetEnvironmentVariable("AGENT_API_KEY", TestApiKey);

        // Spec 049: the local admin login (E2EFixture.SignInAsync uses this password).
        Environment.SetEnvironmentVariable("Auth__Admin__Password", TestAdminPassword);
        E2ETestSignIn.ApplyBindings();

        // Spec 051: the same key as the API host, so the user tokens this host signs are accepted there.
        Environment.SetEnvironmentVariable("UserToken__SigningKey", E2ETestDefaults.UserTokenSigningKey);
        // Spec 051 D10: the web host's Data Protection ring (login cookie, antiforgery) comes from this key.
        Environment.SetEnvironmentVariable("DataProtection__Key", E2ETestDefaults.DataProtectionKey);

        E2ETestDefaults.ApplyDatabaseEnvironment();

        // Spec 045: the monolith fast-fails at startup without a Pipeline API base URL.
        Environment.SetEnvironmentVariable(
            "PipelineApi__BaseUrl", ApiBaseUrl ?? E2ETestDefaults.UnreachableApiBaseUrl);

        // No config caching: the harness seeds through the store between assertions and a stale
        // read would make tests depend on wall-clock timing.
        Environment.SetEnvironmentVariable("PipelineLoop__ConfigCacheTtlSeconds", "0");

        E2ETestDefaults.ApplyDispatchEnvironment();

        E2ETestDefaults.ResetSerilogBootstrapLogger();

        // Set environment to Development so static web assets are resolved correctly.
        builder.UseEnvironment("Development");

        builder.ConfigureServices(services =>
        {
            // Seed default test data
            ConfigStore.SeedDefaults();

            // Spec 049: test-only sign-in for principals other than the local admin.
            services.AddSingleton<Microsoft.AspNetCore.Hosting.IStartupFilter, E2ETestSignIn.StartupFilter>();

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
            // Spec 051: Settings goes through /api/ui/settings. Back it with the same store for the
            // same reason; the API host's template listing, export and import read its database.
            services.RemoveAll<CodingAgent.Web.Ui.IUiSettingsClient>();
            services.AddSingleton<CodingAgent.Web.Ui.IUiSettingsClient>(new CodingAgent.Web.E2ETests.Fakes.InMemoryUiSettingsClient(ApiConfigClient, ConfigStore));

            // Spec 051 2.1c: provider configs and checks go through the real /api/ui/settings client to
            // the API host, which shares this store and FakeProviders and fakes the GitLab check.

            // Same template set as the API host — see E2ETestDefaults.InstallJobTemplates.
            E2ETestDefaults.InstallJobTemplates(services);

            // Leadership is a cluster lease the harness has no way to win, and the work gated on
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

            // PipelineLoopService lives in SchedulerE2EWebApplicationFactory. The web host reads the
            // loop status and sends Start, Stop and Resume through /api/ui/loop on the API host
            // (spec 051 D11); the Scheduler's watchdog applies them. The web calls no Scheduler.

            // Shutdown timeout: must exceed ChatTerminationGracePeriodSeconds (10s) so that
            // Blazor circuits calling TerminateChatSessionAsync on dispose (triggered by
            // E2ETestBase.DisposeAsync closing the browser context at collection cleanup)
            // have time to complete before the host is forcefully shut down.  Without this,
            // the in-flight HTTP call to the API races with IHttpClientFactory disposal,
            // producing [Test Collection Cleanup Failure (E2E)] ObjectDisposedException.
            // 20s gives 10s for the grace period + 10s buffer for the API's WatcherTask.
            services.Configure<HostOptions>(o => o.ShutdownTimeout = TimeSpan.FromSeconds(20));
        });
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

        // Clear the InMemory database.
        //
        // Runs are cleared alongside work items because the harness now shares one database for
        // the whole assembly (see E2ECollection) rather than one per test class. Rows the
        // previous class wrote used to disappear with its host; now they would pile up for the
        // length of the run and show up in another class's queries.
        //
        // Note: ConsolidationRuns table was dropped in migration 20260930213632_DropConsolidationRuns
        // (issue #3031) — PipelineRun is the authoritative record for consolidation runs.
        using (var db = DbContextFactory.CreateDbContext())
        {
            db.WorkItems.RemoveRange(db.WorkItems);
            db.PipelineRuns.RemoveRange(db.PipelineRuns);
            // Clear harness suggestions (stored in KeyValueStore["harness-suggestions"] by
            // PostgresHarnessSuggestionStore). Not clearing this would let Scenario 4 leave
            // stale suggestions visible to subsequent tests.
            db.KeyValueStore.RemoveRange(db.KeyValueStore);
            // Triages that need someone count on Attention: a leftover one changes another test's counts
            db.Triages.RemoveRange(db.Triages);
            db.SaveChanges();
        }

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

    private sealed class NoOpDatabaseProbe : IDatabaseProbe
    {
        public Task ProbeAsync(CancellationToken ct) => Task.CompletedTask;
    }
}
