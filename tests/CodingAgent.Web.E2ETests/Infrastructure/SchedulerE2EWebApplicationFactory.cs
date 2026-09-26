using CodingAgent.Api.Client;
using CodingAgent.Web.E2ETests.Fakes;
using CodingAgent.Infrastructure;
using CodingAgent.Kubernetes;
using CodingAgent.Orchestration;
using CodingAgent.Orchestration.Registry;
using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.LeaderElection;
using CodingAgent.Pipeline.Services;
using CodingAgent.Scheduler;
using CodingAgent.Scheduler.Services;
using CodingAgent.Web.TestUtilities;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using InMemoryConfigurationStore = CodingAgent.Web.E2ETests.Fakes.InMemoryConfigurationStore;

namespace CodingAgent.Web.E2ETests.Infrastructure;

/// <summary>
/// Runs <c>CodingAgent.Scheduler</c> on a real Kestrel port for the E2E harness.
///
/// Paired with <see cref="ApiE2EWebApplicationFactory"/> and <see cref="E2EWebApplicationFactory"/>
/// by <see cref="E2EFixture"/>; the three share a configuration store, run-history service, and
/// provider fakes, so a test seeds once and all processes see it.
///
/// <para>
/// Key design decisions:
/// <list type="bullet">
///   <item>
///     <term>Leader election is disabled.</term>
///     <description>
///     <see cref="LeaderElectionService"/> is removed from DI entirely so
///     <see cref="PipelineLoopServiceDependencies.LeaderElection"/> is <c>null</c> and the loop
///     runs unconditionally. Using <see cref="E2ETestDefaults.InstallKubernetesStub"/> would
///     register a non-null <c>Mock&lt;IKubernetes&gt;</c>, which causes <c>LeaderElectionService</c>
///     to detect a K8s environment and launch the real election loop — <c>_isLeader</c> would
///     never become <c>true</c>, permanently blocking <c>PipelineLoopService.ExecuteAsync</c>.
///     </description>
///   </item>
///   <item>
///     <term><see cref="PipelineLoopService"/> stays hosted.</term>
///     <description>
///     Its <c>ExecuteAsync</c> parks on an activation channel. Unhosting it means
///     <c>StartLoopAsync</c> signals but nothing consumes — <c>IsLoopActive</c> is never set and
///     <c>StopLoop</c> never completes. Only background services that race assertions are removed.
///     </description>
///   </item>
///   <item>
///     <term><see cref="IPipelineApiConfigClient"/> is replaced with an in-memory fake.</term>
///     <description>
///     <c>AutoStartSchedulerLoopAsync</c> calls the config client before <c>RunAsync</c>, with
///     exponential back-off up to ten minutes on failure. The fake returns immediately with
///     <c>ClosedLoopAutoStart = false</c> so auto-start is skipped and test startup is instant.
///     </description>
///   </item>
/// </list>
/// </para>
/// </summary>
public sealed class SchedulerE2EWebApplicationFactory : WebApplicationFactory<SchedulerHostMarker>
{
    private readonly InMemoryConfigurationStore _configStore;
    private readonly InMemoryPipelineRunHistoryService _historyService;
    private readonly FakeProviderFactory _fakeProviders;
    private readonly FakeKubernetesJobClient _fakeK8sClient;
    private readonly string _apiKey;
    private readonly string _pipelineApiBaseUrl;

    public SchedulerE2EWebApplicationFactory(
        string dbName,
        InMemoryConfigurationStore configStore,
        InMemoryPipelineRunHistoryService historyService,
        FakeProviderFactory fakeProviders,
        FakeKubernetesJobClient fakeK8sClient,
        string apiKey,
        string pipelineApiBaseUrl)
    {
        _ = dbName; // accepted for symmetry with ApiE2EWebApplicationFactory; Scheduler has no direct DB access
        _configStore = configStore;
        _historyService = historyService;
        _fakeProviders = fakeProviders;
        _fakeK8sClient = fakeK8sClient;
        _apiKey = apiKey;
        _pipelineApiBaseUrl = pipelineApiBaseUrl;
        UseKestrel(0);
    }

    // TODO [WARNING]: ServerAddress reads ClientOptions.BaseAddress before CreateClient() has been
    // called. WebApplicationFactory<T>.ClientOptions.BaseAddress defaults to http://localhost/ until
    // UseKestrel(0) binds a real port, which only happens after the first CreateClient() call. The
    // current usage in E2EFixture.InitializeAsync is correct (CreateClient() → ServerAddress), but
    // accessing this property from any other context before CreateClient() returns the unbound default
    // http://localhost/ instead of the actual port. Matches the same pattern in ApiE2EWebApplicationFactory.

    /// <summary>Base address of the running Scheduler, e.g. <c>http://localhost:12345</c>.</summary>
    public string ServerAddress => ClientOptions.BaseAddress.ToString().TrimEnd('/');

    /// <summary>
    /// The hosted <see cref="PipelineLoopService"/> singleton — the concrete type is needed so
    /// tests can call <see cref="PipelineLoopService.StopLoop"/> and read
    /// <see cref="PipelineLoopService.IsLoopActive"/>.
    /// </summary>
    public PipelineLoopService LoopService => Services.GetRequiredService<PipelineLoopService>();

    /// <summary>
    /// <see cref="IHousekeepingService"/> — exposed so tests can trigger a single housekeeping
    /// cycle deterministically rather than waiting for the background interval (Requirement 5).
    /// </summary>
    public IHousekeepingService HousekeepingService => Services.GetRequiredService<IHousekeepingService>();

    /// <summary>
    /// <see cref="OrphanedLabelRecoveryService"/> — exposed so tests can call
    /// <c>SweepOnceForTestAsync</c> without waiting for the 30-minute background interval
    /// (Requirement 5). Requires Step 2 of the implementation: the production DI registers this
    /// service as a named singleton so it is resolvable by concrete type.
    /// </summary>
    public OrphanedLabelRecoveryService OrphanedLabelRecovery =>
        Services.GetRequiredService<OrphanedLabelRecoveryService>();

    /// <summary>
    /// Resets per-test state owned by this factory. The shared fakes are reset by
    /// <see cref="E2EFixture.ResetAll"/>; nothing additional is needed here.
    /// </summary>
    // TODO [WARNING]: ResetAll() is intentionally empty because the loop is stopped and reset
    // via E2EFixture.ResetAllAsync (which calls loop.StopLoop() and busy-waits up to 10s before
    // calling this). However, if that 10s deadline expires before IsLoopActive becomes false, this
    // no-op is still called, meaning a stale loop iteration may remain in flight. There is no
    // warning logged when the deadline is exceeded, so a test isolation failure is invisible until
    // the next test fails with an unexpected "Stop Loop" button. Consider adding an assertion or
    // logging when the deadline expires in ResetAllAsync, rather than silently proceeding.
    public void ResetAll() { }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        // Program.cs fast-fails without these two env vars.
        // TODO [WARNING]: These are process-global environment variable writes. Both PipelineApi__BaseUrl
        // and AGENT_API_KEY are also written by E2EWebApplicationFactory and ApiE2EWebApplicationFactory.
        // The start order in E2EFixture.InitializeAsync is deterministic (API → Scheduler → Web), so the
        // final value is correct in normal runs. However, any future test that constructs this factory
        // in isolation will overwrite the value that other running hosts expect. ClearDatabaseEnvironment
        // clears SchedulerApi__BaseUrl but not PipelineApi__BaseUrl or AGENT_API_KEY, which can leak to
        // the next fixture if this factory is disposed before E2EWebApplicationFactory. Prefer
        // builder.UseSetting() / builder.ConfigureAppConfiguration() to avoid process-global state.
        Environment.SetEnvironmentVariable("PipelineApi__BaseUrl", _pipelineApiBaseUrl);
        Environment.SetEnvironmentVariable("AGENT_API_KEY", _apiKey);

        // Match the pattern from ApiE2EWebApplicationFactory.
        E2ETestDefaults.ResetSerilogBootstrapLogger();

        builder.UseEnvironment("Development");

        builder.ConfigureServices(services =>
        {
            // ── Shutdown timeout ──────────────────────────────────────────────────────────────────
            // Match ApiE2EWebApplicationFactory for consistent fast teardown.
            services.Configure<HostOptions>(o => o.ShutdownTimeout = TimeSpan.FromSeconds(5));

            // ── Leader election — MUST be disabled ───────────────────────────────────────────────
            // The Scheduler's own AddSchedulerServices registers LeaderElectionService as a
            // singleton and hosted service. E2ETestDefaults.InstallKubernetesStub installs a
            // non-null Mock<IKubernetes>, which causes LeaderElectionService._isKubernetesEnvironment
            // = true and launches the real K8s election loop → _isLeader stays false →
            // PipelineLoopService.ExecuteAsync is permanently stuck in its leader-wait loop.
            //
            // Fix: remove both registrations so sp.GetService<ILeaderElectionService>() returns
            // null. PipelineLoopServiceDependencies.LeaderElection = null → loop runs
            // unconditionally, matching the E2EWebApplicationFactory pattern.
            services.RemoveAll<LeaderElectionService>();
            services.RemoveAll<ILeaderElectionService>();

            // ── IPipelineApiConfigClient — replace with in-memory fake ────────────────────────────
            // AutoStartSchedulerLoopAsync (runs before app.RunAsync()) calls GetPipelineConfigAsync
            // with exponential back-off up to 10 minutes. The InMemoryPipelineApiConfigClient
            // returns immediately with ClosedLoopAutoStart=false (default), so auto-start is
            // skipped and test startup is instant. Also backs the ApiPipelineConfigStore /
            // ApiProviderConfigStore / ApiProjectStore / ApiConfigurationStore shims that the
            // Scheduler resolves for PipelineLoopServiceDependencies.
            var configClient = new InMemoryPipelineApiConfigClient(_configStore);
            services.RemoveAll<IPipelineApiConfigClient>();
            services.AddSingleton<IPipelineApiConfigClient>(configClient);

            // ── Provider factory and Kubernetes job client ────────────────────────────────────────
            services.RemoveAll<IProviderFactory>();
            services.AddSingleton<IProviderFactory>(_fakeProviders);

            services.RemoveAll<IKubernetesJobClient>();
            services.AddSingleton<IKubernetesJobClient>(_fakeK8sClient);

            // ── Run history service ───────────────────────────────────────────────────────────────
            services.RemoveAll<IPipelineRunHistoryService>();
            services.AddSingleton<IPipelineRunHistoryService>(_historyService);

            // ── Job templates ─────────────────────────────────────────────────────────────────────
            E2ETestDefaults.InstallJobTemplates(services);

            // ── Remove all background services except PipelineLoopService ────────────────────────
            // Use the same global RemoveAll<IHostedService>() pattern as ApiE2EWebApplicationFactory.
            // Re-add PipelineLoopService as the sole hosted service: its ExecuteAsync parks on an
            // activation channel and must stay running for StartLoopAsync/StopLoop to work.
            //
            // Why not selective removal: LoopWatchdogService, OrphanedLabelRecoveryService, etc.
            // are registered as "AddSingleton<T> + AddHostedService(sp => sp.GetRequiredService<T>())".
            // RemoveAll<T>() removes the singleton descriptor but NOT the IHostedService lambda.
            // At host startup the lambda fires, calls GetRequiredService<T>(), and throws because
            // the named singleton was removed. Global removal avoids this.
            //
            // TODO [WARNING]: services.RemoveAll<IHostedService>() removes all IHostedService
            // descriptors registered by ConfigureServices, but ASP.NET Core's hosting infrastructure
            // (GenericWebHostService) registers its own IHostedService *after* ConfigureServices runs
            // (inside the host builder pipeline), so it survives this removal. This is an undocumented
            // implementation detail of WebApplicationFactory<T>. If ASP.NET Core changes the registration
            // order in a future version, or if GenericWebHostService is ever registered before the lambda,
            // the Scheduler test host will silently lose its HTTP listener with no compile or startup error.
            //
            // TODO [WARNING]: OrphanedLabelRecoveryService and FeedbackCommentRelayService are exposed
            // as public properties (OrphanedLabelRecovery, via Services.GetRequiredService<T>()) and their
            // singleton descriptors survive the RemoveAll<IHostedService>() call — so they are resolvable.
            // However, their ExecuteAsync loops never run because no IHostedService registration exists for
            // them. If any test calls SweepOnceForTestAsync() and that method relies on state initialised
            // by ExecuteAsync (channels, timers, semaphores), it will deadlock or throw. Verify those
            // methods are safe to call without ExecuteAsync having run before adding test coverage.
            services.RemoveAll<IHostedService>();
            // Re-add PipelineLoopService as the sole hosted service.
            services.AddHostedService(sp => sp.GetRequiredService<PipelineLoopService>());
        });
    }
}
