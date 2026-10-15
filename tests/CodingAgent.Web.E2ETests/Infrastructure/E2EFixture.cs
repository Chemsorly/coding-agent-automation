using CodingAgent.Api.Client;
using CodingAgent.Web.E2ETests.Fakes;
using CodingAgent.Web.TestUtilities;
using CodingAgent.AgentGateway;
using CodingAgent.Infrastructure.Persistence;
using CodingAgent.Orchestration.Dispatch;
using CodingAgent.Orchestration.Registry;
using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Playwright;

namespace CodingAgent.Web.E2ETests.Infrastructure;

/// <summary>
/// The E2E harness: both processes of the post-Spec-045 topology, created once for the whole
/// assembly and shared through <see cref="E2ECollection"/>. Per-test isolation comes from
/// <see cref="ResetAllAsync"/>, not from rebuilding the hosts.
///
/// The Pipeline API owns <c>/hubs/agent</c>, <c>/api/work-items/*</c> and the database; the
/// Blazor app serves the UI and talks to the API over HTTP. They share an EF InMemory database,
/// the seeded <see cref="ConfigStore"/> and the run-history fake, so a test seeds once and both
/// hosts see it. A <see cref="FakeJobController"/> supplies the dispatch loop that Spec 043 moved
/// into its own process.
///
/// Start order matters: the API must be listening before the Scheduler builds (it uses
/// <c>PipelineApi:BaseUrl</c>), and both must be listening before the Blazor host builds, because
/// the monolith reads <c>PipelineApi:BaseUrl</c> during
/// configuration and fast-fails without them.
///
/// Playwright is started lazily by <see cref="GetBrowserAsync"/>. Tests that assert on state
/// rather than on pages — the <see cref="HeadlessE2ETestBase"/> family — never call it, so they
/// run on a machine with no browser installed.
/// </summary>
public sealed class E2EFixture : IAsyncLifetime
{
    private IPlaywright? _playwright;
    private IBrowser? _browser;
    private readonly SemaphoreSlim _browserLock = new(1, 1);
    private string? _signedInStorageState;
    private readonly SemaphoreSlim _signInLock = new(1, 1);

    public E2EWebApplicationFactory Factory { get; } = new();

    private ApiE2EWebApplicationFactory? _apiFactory;
    private SchedulerE2EWebApplicationFactory? _schedulerFactory;
    private FakeJobController? _jobController;
    // The service clients the Job Controller uses, with the agent API key. The web host has none since spec 051 4.1.
    private ServiceProvider? _serviceClients;
    private JobControllerE2EWebApplicationFactory? _realJobControllerFactory;

    /// <summary>Blazor Server app — UI navigation and page assertions.</summary>
    public string ServerAddress => Factory.ServerAddress;

    /// <summary>
    /// Pipeline API — agents register and report here. Spec 044 removed <c>MapHub&lt;AgentHub&gt;</c>
    /// from the monolith, so connecting to <see cref="ServerAddress"/> fails negotiate with 405.
    /// </summary>
    public string AgentHubUrl => _apiFactory?.ServerAddress
        ?? throw new InvalidOperationException("API host not started");

    public string ApiKey => E2EWebApplicationFactory.TestApiKey;

    // Convenience accessors for fakes
    public InMemoryConfigurationStore ConfigStore => Factory.ConfigStore;
    public FakeProviderFactory FakeProviders => Factory.FakeProviders;
    public InMemoryIssueProvider IssueProvider => Factory.FakeProviders.IssueProvider;
    public InMemoryRepositoryProvider RepositoryProvider => Factory.FakeProviders.RepositoryProvider;
    public ScriptedAgentProvider AgentProvider => Factory.FakeProviders.AgentProvider;
    public ConfigurableQualityGateValidator QualityGateValidator => Factory.QualityGateValidator;
    public InMemoryPipelineRunHistoryService HistoryService => Factory.HistoryService;
    public FakeKubernetesJobClient K8sClient => Factory.FakeK8sClient;
    public IDbContextFactory<PipelineDbContext> DbContextFactory => Factory.DbContextFactory;

    /// <summary>
    /// Agent registry. Lives on the API host — agents register against its hub, so the
    /// monolith's registry is always empty.
    /// </summary>
    public AgentRegistryService AgentRegistry => _apiFactory?.AgentRegistry
        ?? throw new InvalidOperationException("API host not started");

    /// <summary>In-memory run state, owned by the API host alongside the hub.</summary>
    public IOrchestratorRunService RunService => _apiFactory?.RunService
        ?? throw new InvalidOperationException("API host not started");

    /// <summary>
    /// The API host's container, for the services that have no named accessor here.
    ///
    /// <para>
    /// Reach for this whenever a test needs something on the agent-facing side of the split:
    /// <c>IHubContext&lt;AgentHub&gt;</c> above all, since the monolith's copy resolves fine and
    /// then delivers to nobody — every agent connection lives on this host. A test that resolves
    /// one of those from <see cref="Factory"/> does not fail loudly; it waits for a message that
    /// was sent into an empty hub and times out.
    /// </para>
    /// </summary>
    public IServiceProvider ApiServices => _apiFactory?.Services
        ?? throw new InvalidOperationException("API host not started");

    /// <summary>
    /// The Scheduler host. <see cref="CodingAgent.Pipeline.Services.PipelineLoopService"/> lives
    /// here — tests that drive the closed loop must resolve it from this factory rather than from
    /// <see cref="Factory"/> (the Blazor host no longer hosts the loop service).
    /// </summary>
    public SchedulerE2EWebApplicationFactory SchedulerFactory => _schedulerFactory
        ?? throw new InvalidOperationException("Scheduler host not started");

    /// <summary>
    /// Starts the closed loop the way Start does since spec 051 D11: it records the request
    /// (<c>ClosedLoopAutoStart</c>), which the Scheduler's watchdog would otherwise undo, and starts
    /// the loop at once rather than waiting for the watchdog's tick. True when the loop runs.
    /// </summary>
    public async Task<bool> StartLoopAsync()
    {
        await Factory.ConfigStore.UpdatePipelineConfigAsync(c => c with { ClosedLoopAutoStart = true }, CancellationToken.None);
        // The watchdog may start it first, in which case this call finds it running.
        var loop = SchedulerFactory.LoopService;
        return await loop.StartLoopAsync() || loop.IsLoopActive;
    }

    /// <summary>
    /// Stops the closed loop the way Stop does since spec 051 D11: it withdraws the request, so the
    /// Scheduler's watchdog does not start the loop again, and asks the loop to stop after its cycle.
    /// </summary>
    public async Task StopLoopAsync()
    {
        await Factory.ConfigStore.UpdatePipelineConfigAsync(c => c with { ClosedLoopAutoStart = false }, CancellationToken.None);
        SchedulerFactory.LoopService.StopLoop();
    }

    /// <summary>
    /// Chat job dispatcher. Moved to the API host (Spec 044/045) so it polls the registry
    /// that the hub actually writes to. Tests must use this rather than Factory.ChatDispatcher.
    /// </summary>
    public ChatJobDispatcher ChatDispatcher => _apiFactory?.ChatDispatcher
        ?? throw new InvalidOperationException("API host not started");

    /// <summary>
    /// Run lifecycle manager. Spec 045 removed it from the monolith's DI and re-homed it in the
    /// API alongside the run state it mutates, so tests must resolve it from that host.
    /// </summary>
    public IRunLifecycleManager RunLifecycleManager => _apiFactory?.Services
        .GetRequiredService<IRunLifecycleManager>()
        ?? throw new InvalidOperationException("API host not started");

    /// <summary>
    /// A work-item client with the agent API key, pointed at the API host: the one the Job Controller
    /// uses. Lets a test read queue state (pending, claimed, status) over the real endpoints
    /// instead of reaching into the database behind them.
    /// </summary>
    public IPipelineApiWorkItemClient WorkItems =>
        _serviceClients?.GetRequiredService<IPipelineApiWorkItemClient>()
        ?? throw new InvalidOperationException("API host not started");

    /// <summary>
    /// Stands in for the Job Controller process. Nothing else moves a WorkItem out of
    /// <c>Pending</c> since Spec 043 moved the dispatch loop out of the monolith.
    /// </summary>
    public FakeJobController JobController => _jobController
        ?? throw new InvalidOperationException("Job controller not started");

    /// <summary>
    /// The real <see cref="CodingAgent.JobController.Reconciliation.ReconciliationLoop"/> hosted
    /// in-process against the API host, with <see cref="FakeKubernetesJobClient"/> and the
    /// always-leader stub wired in. Tests call its methods directly (deterministically, not via
    /// the polling loop) to exercise reconciliation paths that <see cref="FakeJobController"/>
    /// never reaches.
    /// </summary>
    // TODO [WARNING]: The ReconciliationLoop singleton holds a process-lifetime
    // HashSet<Guid> _reconciledTerminalIds that is only cleared by OnLeadershipAcquired(),
    // which nothing in the harness ever calls (RemoveAll<IHostedService>() removes the only
    // production caller, ReconciliationService). If two tests ever reconcile the same WorkItem
    // GUID, the second ReconcileOnceAsync would silently skip it via the
    // _reconciledTerminalIds.Contains guard. Fix: call loop.OnLeadershipAcquired() in
    // ResetAllAsync (or expose a dedicated reset hook) so the cache is cleared between tests.
    public CodingAgent.JobController.Reconciliation.ReconciliationLoop RealReconciliationLoop =>
        _realJobControllerFactory?.ReconciliationLoop
        ?? throw new InvalidOperationException("Real JobController factory not started");

    public Task InitializeAsync()
    {
        _apiFactory = new ApiE2EWebApplicationFactory(
            Factory.DbName,
            Factory.ConfigStore,
            Factory.HistoryService,
            Factory.FakeProviders,
            Factory.FakeK8sClient,
            ApiKey);

        // CreateClient() forces the host to build and Kestrel to bind.
        using (var apiClient = _apiFactory.CreateClient()) { }

        Factory.ApiBaseUrl = _apiFactory.ServerAddress;

        // Build the Scheduler host after the API is listening, so PipelineApi__BaseUrl is
        // resolvable.
        _schedulerFactory = new SchedulerE2EWebApplicationFactory(
            Factory.DbName,
            Factory.ConfigStore,
            Factory.HistoryService,
            Factory.FakeProviders,
            Factory.FakeK8sClient,
            ApiKey,
            _apiFactory.ServerAddress);

        // Force the Scheduler host to build and bind its Kestrel port.
        using (var schedulerClient = _schedulerFactory.CreateClient()) { }


        using var appClient = Factory.CreateClient();

        // The controller claims over HTTP with the agent API key, exactly as the real one does.
        _serviceClients = new ServiceCollection()
            .AddPipelineApiClient(new PipelineApiClientOptions { BaseUrl = _apiFactory.ServerAddress, AgentApiKey = ApiKey })
            .BuildServiceProvider();
        _jobController = new FakeJobController(
            WorkItems,
            _apiFactory.AgentRegistry,
            DbContextFactory);

        // Build the real JobController in-process against the API host.
        // Must be built after _apiFactory so ServerAddress is known.
        _realJobControllerFactory = new JobControllerE2EWebApplicationFactory(
            _apiFactory.ServerAddress,
            Factory.FakeK8sClient,
            ApiKey);
        // Force the host to build and DI to resolve (triggers ConfigureWebHost overrides).
        // TODO [WARNING]: CreateClient() to force EnsureServer() is fragile — if a future
        // ConfigureWebHost change reintroduces a hosted service, the immediately-disposed client
        // would race with service startup and silently discard async host-start exceptions. A more
        // explicit alternative is: _ = _realJobControllerFactory.Services; (accessing Services
        // also triggers EnsureServer without creating a disposable client).
        using (var jcClient = _realJobControllerFactory.CreateClient()) { }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Clears per-test state across <em>both</em> hosts and the fake job controller.
    ///
    /// Tests must call this rather than <c>Fixture.Factory.ResetAll()</c>, which only knows about
    /// the monolith. The agent registry and run state moved to the API host in Spec 044, so
    /// resetting the monolith alone leaves the state that actually matters untouched.
    /// </summary>
    public void ResetAll()
    {
        Factory.ResetAll();
        _apiFactory?.ResetAll();
        _schedulerFactory?.ResetAll();
        _jobController?.ForgetAllInFlight();
        // Clear claimed IDs so assertions in each test are not polluted by earlier test runs.
        // Without this, Assert.Single / Assert.Equal(2, ...) / Assert.Empty assertions on
        // ClaimedWorkItemIds across tests read an ever-growing list and fail depending on order.
        _jobController?.ClearClaimedWorkItemIds();
    }

    /// <summary>
    /// <see cref="ResetAll"/> plus a stopped pipeline loop.
    ///
    /// <para>
    /// <c>PipelineLoopService</c> is a singleton that survives the test that started it, and
    /// <see cref="ResetAll"/> cannot stop it on its own: <c>StopLoop</c> only <em>requests</em> a
    /// stop and the loop finishes its current cycle first. A test that started the loop therefore
    /// handed the next one a page showing "Loop stopping… (finishing current run)" and a Stop Loop
    /// button where it expected Start Loop — which is what <c>LoopControlTests</c> kept failing on,
    /// with no hint that the cause was the previous test.
    /// </para>
    /// </summary>
    public async Task ResetAllAsync()
    {
        // Withdraw a Start first: the Scheduler's watchdog would otherwise restart the loop between
        // the stop below and ResetAll (spec 051 D11).
        await Factory.ConfigStore.UpdatePipelineConfigAsync(c => c with { ClosedLoopAutoStart = false }, CancellationToken.None);

        // PipelineLoopService lives in the Scheduler host; stop it there.
        if (_schedulerFactory is not null)
        {
            // Guard against ObjectDisposedException: if the scheduler host stopped (e.g. because a
            // prior test left the loop mid-cycle and an unhandled exception propagated), accessing
            // LoopService throws ObjectDisposedException. In that case the loop is definitionally
            // stopped (the host is gone), so we can proceed to ResetAll() without waiting.
            PipelineLoopService? loop = null;
            try { loop = _schedulerFactory.LoopService; }
            catch (ObjectDisposedException) { /* scheduler host already stopped; loop is no longer active */ }

            if (loop is not null)
            {
                loop.StopLoop();

                var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
                // TODO [WARNING]: This busy-wait has no CancellationToken, so callers cannot interrupt
                // early (e.g. via xUnit's test timeout). If StopLoop() fails to drive IsLoopActive to
                // false before the 10s deadline (e.g. the loop hangs in a long cycle under CI load),
                // ResetAll() is still called with the loop technically active, leaving a stale iteration
                // in flight when the next test begins. The deadline expiry is silent — consider logging
                // a warning so test pollution is visible in CI output rather than manifesting as a
                // confusing failure in the next test.
                while (loop.IsLoopActive && DateTime.UtcNow < deadline)
                    await Task.Delay(25);
            }
        }

        ResetAll();
    }

    /// <summary>
    /// An operator-authenticated client for the Pipeline API.
    ///
    /// <c>/api/work-items/*</c> moved to the API host in Spec 042 and the monolith stopped
    /// mapping those routes, so a client built from <see cref="Factory"/> answers 405. Tests that
    /// exercise the endpoints an agent pod calls must go through this one.
    /// </summary>
    /// <param name="authenticated">
    /// Pass <c>false</c> for the tests that assert the endpoints reject anonymous callers.
    /// Without it they get an authenticated client and see 404 for a nonexistent work item
    /// rather than the 401 they are checking for.
    /// </param>
    public HttpClient CreateApiClient(bool authenticated = true)
    {
        var client = (_apiFactory ?? throw new InvalidOperationException("API host not started"))
            .CreateClient();
        if (authenticated)
            client.DefaultRequestHeaders.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", ApiKey);
        return client;
    }

    /// <summary>
    /// A client for the API's UI surface (<c>/api/ui/**</c>, spec 051 D1), which takes only user tokens: it
    /// carries one for "tester" with <paramref name="grant"/> (global admin by default), signed with the
    /// harness key the way the web host signs them.
    /// </summary>
    public HttpClient CreateUiApiClient(CodingAgent.Contracts.Auth.AccessGrant? grant = null)
    {
        var client = (_apiFactory ?? throw new InvalidOperationException("API host not started")).CreateClient();
        var now = DateTimeOffset.UtcNow;
        var token = CodingAgent.Infrastructure.Common.Auth.UserTokens.Create(
            new CodingAgent.Infrastructure.Common.Auth.UserTokenSubject("e2e:tester", "tester", "e2e"),
            grant ?? new CodingAgent.Contracts.Auth.AccessGrant(CodingAgent.Contracts.Auth.AccessRole.Admin, new Dictionary<string, CodingAgent.Contracts.Auth.AccessRole>(), []),
            now,
            now.AddMinutes(30),
            CodingAgent.Infrastructure.Common.Auth.UserTokens.CreateSigningKey(E2ETestDefaults.UserTokenSigningKey));
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    /// <summary>
    /// Returns the shared Playwright browser, launching it on first use.
    ///
    /// Launching costs about a second and requires the Chromium bundle to be installed, so it is
    /// deferred rather than done in <see cref="InitializeAsync"/>: the state-assertion suites
    /// never touch a page and should not pay for one or depend on one being present.
    /// </summary>
    public async Task<IBrowser> GetBrowserAsync()
    {
        if (_browser is not null) return _browser;

        await _browserLock.WaitAsync();
        try
        {
            if (_browser is null)
            {
                _playwright = await Playwright.CreateAsync();
                _browser = await _playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
                {
                    Headless = true
                });
            }
        }
        finally
        {
            _browserLock.Release();
        }

        return _browser;
    }

    /// <summary>
    /// Playwright storage state of a browser signed in as the local admin (Spec 049: every page
    /// requires a signed-in user). The login goes through the real login form once per fixture;
    /// every browser context then starts from this state instead of logging in again.
    /// </summary>
    public async Task<string> GetSignedInStorageStateAsync()
    {
        if (_signedInStorageState is not null) return _signedInStorageState;

        await _signInLock.WaitAsync();
        try
        {
            if (_signedInStorageState is null)
            {
                var browser = await GetBrowserAsync();
                await using var context = await browser.NewContextAsync();
                await E2ETestBase.StubExternalFontsAsync(context);
                var page = await context.NewPageAsync();
                await SignInAsync(page, ServerAddress);
                _signedInStorageState = await context.StorageStateAsync();
            }
        }
        finally
        {
            _signInLock.Release();
        }

        return _signedInStorageState;
    }

    /// <summary>Signs <paramref name="page"/> in through the login form and waits until it left /login.</summary>
    public static async Task SignInAsync(IPage page, string serverAddress, string password = E2EWebApplicationFactory.TestAdminPassword)
    {
        await page.GotoAsync($"{serverAddress}/login");
        await page.FillAsync("[data-testid=login-password]", password);
        await page.ClickAsync("[data-testid=login-submit]");
        await page.WaitForURLAsync(url => !url.Contains("/login", StringComparison.OrdinalIgnoreCase),
            new PageWaitForURLOptions { Timeout = 30_000 });
    }

    public async Task DisposeAsync()
    {
        if (_browser is not null)
            await _browser.DisposeAsync();
        _playwright?.Dispose();
        _browserLock.Dispose();
        _signInLock.Dispose();

        if (_jobController is not null)
            await _jobController.DisposeAsync();
        if (_serviceClients is not null)
            await _serviceClients.DisposeAsync();

        // Dispose hosts in dependency order: Blazor → Scheduler → API → JobController. The Scheduler's
        // loop calls the API, so it stops before the API goes away (#3181).
        await DisposeHostAsync(Factory);
        await DisposeHostAsync(_schedulerFactory);
        await DisposeHostAsync(_apiFactory);
        await DisposeHostAsync(_realJobControllerFactory);

        E2ETestDefaults.ClearDatabaseEnvironment();
    }

    private static async Task DisposeHostAsync(IAsyncDisposable? host)
    {
        if (host is null)
            return;
        try
        {
            await host.DisposeAsync();
        }
        catch (ObjectDisposedException)
        {
            // Chat-test circuit teardown runs async TerminateChatSessionAsync calls that can still be in
            // flight when a host's DI container disposes its singletons. That race is not a test failure,
            // and reporting it as a collection-cleanup failure would hide real ones (#3181).
        }
    }
}
