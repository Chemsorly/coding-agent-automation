using CodingAgentWebUI.Api;
using CodingAgentWebUI.E2ETests.Fakes;
using CodingAgentWebUI.Hub;
using CodingAgentWebUI.Infrastructure;
using CodingAgentWebUI.Infrastructure.Locking;
using CodingAgentWebUI.Infrastructure.Persistence;
using CodingAgentWebUI.Kubernetes;
using CodingAgentWebUI.Orchestration;
using CodingAgentWebUI.Orchestration.Dispatch;
using CodingAgentWebUI.Orchestration.Redis;
using CodingAgentWebUI.Orchestration.Registry;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.TestUtilities;
using InMemoryConfigurationStore = CodingAgentWebUI.E2ETests.Fakes.InMemoryConfigurationStore;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Moq;

namespace CodingAgentWebUI.E2ETests.Infrastructure;

/// <summary>
/// Runs <c>CodingAgentWebUI.Api</c> on a real Kestrel port for the E2E harness.
///
/// From Spec 044 the API is the sole host of <c>/hubs/agent</c> and <c>/api/work-items/*</c> —
/// the monolith's <c>MapHub</c> is gone — so a fake agent that connects to the Blazor app gets a
/// 405 on negotiate. The harness therefore runs both processes, and this factory is the half the
/// agents talk to.
///
/// Both hosts live in the same test process, so shared state is passed in rather than
/// synchronised: the same EF InMemory database name, the same seeded
/// <see cref="InMemoryConfigurationStore"/>, and the same run-history fake the assertions read.
///
/// Targets <see cref="ApiHostMarker"/> rather than <c>Program</c>: both assemblies expose a
/// global <c>Program</c> and this project references both.
/// </summary>
public sealed class ApiE2EWebApplicationFactory : WebApplicationFactory<ApiHostMarker>
{
    private readonly string _dbName;
    private readonly InMemoryConfigurationStore _configStore;
    private readonly InMemoryPipelineRunHistoryService _historyService;
    private readonly FakeProviderFactory _fakeProviders;
    private readonly FakeKubernetesJobClient _fakeK8sClient;
    private readonly string _apiKey;

    /// <summary>
    /// When non-null, the factory replaces <see cref="IAgentRegistryService"/>,
    /// <see cref="IOrchestratorRunService"/>, and <see cref="AgentReservationService"/> with their
    /// distributed implementations backed by this shared store. Used by
    /// <see cref="MultiReplicaE2EFixture"/> to simulate two API replicas sharing Redis state.
    /// </summary>
    private readonly FakeRedisStore? _sharedRedisStore;

    public ApiE2EWebApplicationFactory(
        string dbName,
        InMemoryConfigurationStore configStore,
        InMemoryPipelineRunHistoryService historyService,
        FakeProviderFactory fakeProviders,
        FakeKubernetesJobClient fakeK8sClient,
        string apiKey,
        FakeRedisStore? sharedRedisStore = null)
    {
        _dbName = dbName;
        _configStore = configStore;
        _historyService = historyService;
        _fakeProviders = fakeProviders;
        _fakeK8sClient = fakeK8sClient;
        _apiKey = apiKey;
        _sharedRedisStore = sharedRedisStore;
        UseKestrel(0);
    }

    /// <summary>Base address of the running API, e.g. <c>http://localhost:12345</c>.</summary>
    public string ServerAddress => ClientOptions.BaseAddress.ToString().TrimEnd('/');

    /// <summary>Agent registry — agents register here, not on the monolith.</summary>
    public AgentRegistryService AgentRegistry => Services.GetRequiredService<AgentRegistryService>();

    /// <summary>In-memory run state — owned by the API since the hub moved.</summary>
    public IOrchestratorRunService RunService => Services.GetRequiredService<IOrchestratorRunService>();

    /// <summary>
    /// Chat job dispatcher — moved to the API host (Spec 044/045 follow-up) so it polls the
    /// registry that the hub actually writes to.
    /// </summary>
    public ChatJobDispatcher ChatDispatcher => Services.GetRequiredService<ChatJobDispatcher>();

    /// <summary>
    /// Clears the per-test state this host owns.
    ///
    /// Both of these live here rather than in the monolith because Spec 044 moved the hub, and
    /// neither was being reset between tests: an agent connected by one test stayed Idle in the
    /// registry for every test that followed. That is not merely stale data — <see
    /// cref="FakeJobController"/> claims Pending work for any idle agent it finds, so a leftover
    /// agent silently consumed work items belonging to tests that meant to drive dispatch
    /// themselves, and those tests failed with "expected 1 job, got 0".
    /// </summary>
    public void ResetAll()
    {
        Services.GetRequiredService<AgentRegistryService>().Reset();
        Services.GetRequiredService<OrchestratorRunService>().Reset();
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        E2ETestDefaults.ApplyDatabaseEnvironment();
        Environment.SetEnvironmentVariable("AGENT_API_KEY", _apiKey);

        // Chat dispatch reads its namespace, PVC credential pool and pod timeouts from
        // configuration through DispatchServiceOptionsFactory. The Blazor factory also applies
        // these, but this host is built first, so relying on that ordering would leave the API's
        // options at their defaults — an empty credential pool, which makes every kiro chat
        // dispatch fail to claim a PVC.
        E2ETestDefaults.ApplyDispatchEnvironment();

        E2ETestDefaults.ResetSerilogBootstrapLogger();

        builder.UseEnvironment("Development");

        builder.ConfigureServices(services =>
        {
            services.Configure<HostOptions>(o => o.ShutdownTimeout = TimeSpan.FromSeconds(5));

            // Background loops (dispatch, reconciliation, leader election, retention sweeps) would
            // race the assertions. The hub is not a hosted service, so it survives this.
            services.RemoveAll<IHostedService>();

            E2EInMemoryDatabase.Install(services, _dbName);

            services.RemoveAll<IDistributedLockProvider>();
            services.AddDistributedLockProvider(null);

            services.RemoveAll<IDatabaseProbe>();
            services.AddSingleton<IDatabaseProbe>(new NoOpDatabaseProbe());

            // Config comes from the same store the tests seed, so the API and the Blazor app agree
            // on templates, profiles and provider configs without a round-trip to Postgres.
            ReplaceSingleton<IConfigurationStore>(services, _configStore);
            ReplaceSingleton<IPipelineConfigStore>(services, _configStore);
            ReplaceSingleton<IProviderConfigStore>(services, _configStore);
            ReplaceSingleton<IAgentProfileStore>(services, _configStore);
            ReplaceSingleton<IQualityGateConfigStore>(services, _configStore);
            ReplaceSingleton<IReviewerConfigStore>(services, _configStore);
            ReplaceSingleton<IProjectStore>(services, _configStore);

            // Run history is asserted directly by the tests via the same instance.
            ReplaceSingleton<IPipelineRunHistoryService>(services, _historyService);

            // No real GitHub / Kiro CLI / Kubernetes in the harness.
            // Use the same FakeProviderFactory instance as the Blazor host so AgentHub's issue
            // operations (RequestCreateIssue, RequestCreateEpicIssue etc.) call through to the
            // in-memory fake rather than a null mock.CreateIssueProvider → NullReferenceException.
            ReplaceSingleton<IProviderFactory>(services, _fakeProviders);
            ReplaceSingleton(services, new Mock<IQualityGateValidator>().Object);

            // The *same* fake cluster the Blazor host writes to. ChatJobDispatcher moved to this
            // host, so its Jobs land in whichever fake this registration names — and the tests
            // assert against the Blazor host's instance via Fixture.K8sClient. A second instance
            // here meant every chat Job was created into a fake nobody read, which reads as
            // "no Job was created" in every chat assertion.
            services.RemoveAll<IKubernetesJobClient>();
            services.AddSingleton<IKubernetesJobClient>(_fakeK8sClient);
            E2ETestDefaults.InstallKubernetesStub(services);

            // Spec 043 moved JobTemplateStore into the API too.
            E2ETestDefaults.InstallJobTemplates(services);

            // Replace AssignmentEnricher with a passthrough stub so E2E tests that exercise the
            // /assignment endpoint don't fail due to missing real dependencies (IProviderFactory,
            // IAgentProfileStore resolved with no seeded profiles, etc.).
            // Enrichment correctness is covered by CodingAgentWebUI.Api.IntegrationTests.
            // The passthrough returns the identity request as-is — sufficient for E2E workflow tests.
            services.RemoveAll<AssignmentEnricher>();
            services.AddSingleton<AssignmentEnricher>(new PassthroughAssignmentEnricher());

            // ── Multi-replica: inject distributed services backed by FakeRedisStore ──
            // When a shared FakeRedisStore is provided (by MultiReplicaE2EFixture), replace the
            // in-memory service registrations with their distributed Redis-backed equivalents.
            // Both replica factories receive the *same* FakeRedisStore instance, so state written
            // by one replica is immediately visible to the other — simulating shared Redis.
            //
            // The DI factories in AddApiOrchestration construct new RedisStore(mux.GetDatabase())
            // directly rather than resolving IRedisStore, so we must replace the registrations
            // here rather than registering IRedisStore into the container.
            if (_sharedRedisStore is not null)
            {
                var store = _sharedRedisStore;

                // IAgentRegistryService → DistributedAgentRegistryService
                services.RemoveAll<IAgentRegistryService>();
                var distributedRegistry = new DistributedAgentRegistryService(store, Serilog.Log.Logger);
                services.AddSingleton<IAgentRegistryService>(distributedRegistry);

                // IOrchestratorRunService → DistributedRunService
                // IsIssueDistributedAsync: use a no-op delegate returning false
                // (no active work item) — correct for all multi-replica test scenarios.
                services.RemoveAll<IOrchestratorRunService>();
                services.AddSingleton<IOrchestratorRunService>(_ =>
                    new DistributedRunService(
                        store,
                        (_, _, _) => Task.FromResult(false),
                        Serilog.Log.Logger));

                // AgentReservationService with the distributed Redis store
                services.RemoveAll<AgentReservationService>();
                services.AddSingleton<AgentReservationService>(sp =>
                    new AgentReservationService(sp.GetRequiredService<IAgentRegistryService>(), Serilog.Log.Logger, store));
            }
        });
    }

    private static void ReplaceSingleton<T>(IServiceCollection services, T instance) where T : class
    {
        services.RemoveAll<T>();
        services.AddSingleton(instance);
    }

    private sealed class NoOpDatabaseProbe : IDatabaseProbe
    {
        public Task ProbeAsync(CancellationToken ct) => Task.CompletedTask;
    }

    /// <summary>
    /// Passthrough AssignmentEnricher for E2E tests. Returns the identity request as-is.
    /// Replaces the real <see cref="AssignmentEnricher"/> whose <see cref="DispatchInfrastructure"/>
    /// dependency needs a seeded agent profile — which E2E K8s tests don't provide because they
    /// test workflow behaviour, not enrichment correctness.
    /// </summary>
    private sealed class PassthroughAssignmentEnricher : AssignmentEnricher
    {
        public PassthroughAssignmentEnricher() : base(Serilog.Log.Logger) { }

        public override Task<JobDistributionRequest?> EnrichAsync(
            JobDistributionRequest identity, PipelineProject project, CancellationToken ct)
            => Task.FromResult<JobDistributionRequest?>(identity);
    }
}
