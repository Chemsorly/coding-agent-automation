using CodingAgentWebUI.Api.Client;
using CodingAgentWebUI.E2ETests.Fakes;
using CodingAgentWebUI.Infrastructure.Persistence;
using CodingAgentWebUI.Orchestration.Registry;
using CodingAgentWebUI.Pipeline.Interfaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Playwright;

namespace CodingAgentWebUI.E2ETests.Infrastructure;

/// <summary>
/// The E2E harness: both processes of the post-Spec-045 topology, created once per test class
/// via <c>IClassFixture</c>.
///
/// The Pipeline API owns <c>/hubs/agent</c>, <c>/api/work-items/*</c> and the database; the
/// Blazor app serves the UI and talks to the API over HTTP. They share an EF InMemory database,
/// the seeded <see cref="ConfigStore"/> and the run-history fake, so a test seeds once and both
/// hosts see it. A <see cref="FakeJobController"/> supplies the dispatch loop that Spec 043 moved
/// into its own process.
///
/// Start order matters: the API must be listening before the Blazor host builds, because the
/// monolith reads <c>PipelineApi:BaseUrl</c> during configuration and fast-fails without it.
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

    public E2EWebApplicationFactory Factory { get; } = new();

    private ApiE2EWebApplicationFactory? _apiFactory;
    private FakeJobController? _jobController;

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
    /// Run lifecycle manager. Spec 045 removed it from the monolith's DI and re-homed it in the
    /// API alongside the run state it mutates, so tests must resolve it from that host.
    /// </summary>
    public IRunLifecycleManager RunLifecycleManager => _apiFactory?.Services
        .GetRequiredService<IRunLifecycleManager>()
        ?? throw new InvalidOperationException("API host not started");

    /// <summary>
    /// Stands in for the Job Controller process. Nothing else moves a WorkItem out of
    /// <c>Pending</c> since Spec 043 moved the dispatch loop out of the monolith.
    /// </summary>
    public FakeJobController JobController => _jobController
        ?? throw new InvalidOperationException("Job controller not started");

    public Task InitializeAsync()
    {
        _apiFactory = new ApiE2EWebApplicationFactory(
            Factory.DbName,
            Factory.ConfigStore,
            Factory.HistoryService,
            ApiKey);

        // CreateClient() forces the host to build and Kestrel to bind.
        using (var apiClient = _apiFactory.CreateClient()) { }

        Factory.ApiBaseUrl = _apiFactory.ServerAddress;
        using var appClient = Factory.CreateClient();

        // The work-item client is registered in the monolith by AddPipelineApiClient and points at
        // PipelineApi:BaseUrl — the API host started above — so the controller claims over HTTP
        // exactly as the real one does.
        _jobController = new FakeJobController(
            Factory.Services.GetRequiredService<IPipelineApiWorkItemClient>(),
            _apiFactory.AgentRegistry);

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
        _jobController?.ForgetAllInFlight();
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

    public async Task DisposeAsync()
    {
        if (_browser is not null)
            await _browser.DisposeAsync();
        _playwright?.Dispose();
        _browserLock.Dispose();

        if (_jobController is not null)
            await _jobController.DisposeAsync();

        await Factory.DisposeAsync();
        if (_apiFactory is not null)
            await _apiFactory.DisposeAsync();

        E2ETestDefaults.ClearDatabaseEnvironment();
    }
}
