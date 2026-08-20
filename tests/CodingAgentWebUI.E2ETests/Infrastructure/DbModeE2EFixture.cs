using CodingAgentWebUI.Api.Client;
using CodingAgentWebUI.E2ETests.Fakes;
using CodingAgentWebUI.Infrastructure.Persistence;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Orchestration.Registry;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace CodingAgentWebUI.E2ETests.Infrastructure;

/// <summary>
/// Shared fixture for DB-mode E2E tests. Creates the hosts once per test class
/// (via IClassFixture). No Playwright — these are SignalR + DB assertion tests.
///
/// Two hosts, matching the post-Spec-044 topology: the Blazor app, and the Pipeline API that
/// owns <c>/hubs/agent</c> and <c>/api/work-items/*</c>. Fake agents connect to
/// <see cref="AgentHubUrl"/>; browser navigation and UI assertions use
/// <see cref="ServerAddress"/>. The two share an EF InMemory database, the seeded
/// <see cref="ConfigStore"/> and the run-history fake, so a test seeds once and both hosts see it.
///
/// Start order matters: the API must be listening before the Blazor host builds, because the
/// monolith reads <c>PipelineApi:BaseUrl</c> during configuration and fast-fails without it.
/// </summary>
public sealed class DbModeE2EFixture : IAsyncLifetime
{
    public DbModeE2EWebApplicationFactory Factory { get; } = new();

    private ApiE2EWebApplicationFactory? _apiFactory;

    /// <summary>Blazor Server app — UI navigation and page assertions.</summary>
    public string ServerAddress => Factory.ServerAddress;

    /// <summary>Pipeline API — agents register and report here.</summary>
    public string AgentHubUrl => _apiFactory?.ServerAddress
        ?? throw new InvalidOperationException("API host not started");

    public string ApiKey => DbModeE2EWebApplicationFactory.TestApiKey;

    // Convenience accessors
    public InMemoryConfigurationStore ConfigStore => Factory.ConfigStore;
    public FakeProviderFactory FakeProviders => Factory.FakeProviders;
    public InMemoryIssueProvider IssueProvider => Factory.FakeProviders.IssueProvider;
    public InMemoryRepositoryProvider RepositoryProvider => Factory.FakeProviders.RepositoryProvider;
    public ConfigurableQualityGateValidator QualityGateValidator => Factory.QualityGateValidator;
    public InMemoryPipelineRunHistoryService HistoryService => Factory.HistoryService;
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

    private FakeJobController? _jobController;

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
        using var apiClient = _apiFactory.CreateClient();

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

    public async Task DisposeAsync()
    {
        if (_jobController is not null)
            await _jobController.DisposeAsync();
        await Factory.DisposeAsync();
        if (_apiFactory is not null)
            await _apiFactory.DisposeAsync();
        E2ETestDefaults.ClearDatabaseEnvironment();
    }
}
