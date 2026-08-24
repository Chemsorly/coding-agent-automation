using CodingAgentWebUI.E2ETests.Fakes;
using CodingAgentWebUI.Orchestration;
using CodingAgentWebUI.Orchestration.Dispatch;
using CodingAgentWebUI.Orchestration.Registry;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.TestUtilities;
using InMemoryConfigurationStore = CodingAgentWebUI.E2ETests.Fakes.InMemoryConfigurationStore;
using Microsoft.Extensions.DependencyInjection;

namespace CodingAgentWebUI.E2ETests.Infrastructure;

/// <summary>
/// Two <see cref="ApiE2EWebApplicationFactory"/> instances sharing a single
/// <see cref="FakeRedisStore"/>. Simulates two API replicas backed by shared Redis state without
/// requiring Docker or a real Redis process.
///
/// <para>
/// Both replicas use <see cref="DistributedAgentRegistryService"/>,
/// <see cref="DistributedRunService"/>, and <see cref="AgentReservationService"/> (with Redis store)
/// constructed from the same <see cref="FakeRedisStore"/> instance. State written by Replica1 is
/// immediately visible to Replica2 — no pub/sub latency, no network round-trip.
/// </para>
///
/// <para>
/// <b>Known limitation:</b> <see cref="FakeRedisStore.ScriptEvaluateAsync"/> does not reproduce
/// Lua script atomicity. <c>RemoveRun</c> uses SREM + EXPIREAT in a single Lua script on real
/// Redis; in this fixture two concurrent callers can both observe "key exists" before either
/// removes it. This tradeoff is accepted — Lua atomicity requires a real Redis process. All other
/// cross-replica invariants (SETNX via <c>TryAdd</c>, set/hash visibility, TTL tracking) are
/// faithfully reproduced.
/// </para>
///
/// <para>
/// Unlike <see cref="E2EFixture"/> this fixture does not start the Blazor monolith, the
/// <see cref="FakeJobController"/>, or Playwright. Tests derive from
/// <see cref="MultiReplicaTestBase"/> and interact directly with the hub and service layer.
/// </para>
/// </summary>
public sealed class MultiReplicaE2EFixture : IAsyncLifetime
{
    /// <summary>
    /// The single shared Redis store. Both replicas read and write the same instance,
    /// simulating a shared Redis backend.
    /// </summary>
    public FakeRedisStore SharedRedisStore { get; } = new();

    public ApiE2EWebApplicationFactory Replica1 { get; private set; } = null!;
    public ApiE2EWebApplicationFactory Replica2 { get; private set; } = null!;

    private const string ApiKey = E2EWebApplicationFactory.TestApiKey;

    // Shared fakes — both replicas use the same instances so test assertions
    // against config, history, and providers work regardless of which replica handles a request.
    private readonly InMemoryConfigurationStore _configStore = new();
    private readonly InMemoryPipelineRunHistoryService _historyService = new();
    private readonly FakeProviderFactory _fakeProviders = new();
    private readonly FakeKubernetesJobClient _fakeK8sClient = new();

    public string AgentHubUrl1 => Replica1.ServerAddress;
    public string AgentHubUrl2 => Replica2.ServerAddress;
    public string ApiKeyValue => ApiKey;

    public InMemoryConfigurationStore ConfigStore => _configStore;
    public InMemoryPipelineRunHistoryService HistoryService => _historyService;

    /// <summary>
    /// The <see cref="IAgentRegistryService"/> resolved from Replica1.
    /// On the distributed path this is a <see cref="DistributedAgentRegistryService"/> backed by
    /// <see cref="SharedRedisStore"/>.
    /// </summary>
    public IAgentRegistryService Registry1 => Replica1.Services.GetRequiredService<IAgentRegistryService>();

    /// <summary>
    /// The <see cref="IAgentRegistryService"/> resolved from Replica2.
    /// Different object instance than <see cref="Registry1"/> but shares the same
    /// <see cref="FakeRedisStore"/>.
    /// </summary>
    public IAgentRegistryService Registry2 => Replica2.Services.GetRequiredService<IAgentRegistryService>();

    public IOrchestratorRunService RunService1 => Replica1.Services.GetRequiredService<IOrchestratorRunService>();
    public IOrchestratorRunService RunService2 => Replica2.Services.GetRequiredService<IOrchestratorRunService>();

    public AgentReservationService ReservationService1 => Replica1.Services.GetRequiredService<AgentReservationService>();
    public AgentReservationService ReservationService2 => Replica2.Services.GetRequiredService<AgentReservationService>();

    public Task InitializeAsync()
    {
        _configStore.SeedDefaults();

        var dbName = $"MultiReplica-{Guid.NewGuid()}";

        Replica1 = new ApiE2EWebApplicationFactory(
            dbName,
            _configStore,
            _historyService,
            _fakeProviders,
            _fakeK8sClient,
            ApiKey,
            sharedRedisStore: SharedRedisStore);

        Replica2 = new ApiE2EWebApplicationFactory(
            dbName,
            _configStore,
            _historyService,
            _fakeProviders,
            _fakeK8sClient,
            ApiKey,
            sharedRedisStore: SharedRedisStore);

        // CreateClient() forces Kestrel to bind on both replicas.
        using (Replica1.CreateClient()) { }
        using (Replica2.CreateClient()) { }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Resets per-test state: clears the shared Redis store and both replicas'
    /// in-memory fallback registries/run services.
    /// </summary>
    public void ResetAll()
    {
        SharedRedisStore.Reset();
        _configStore.Reset();
        _configStore.SeedDefaults();
        _historyService.Reset();
        _fakeProviders.Reset();
        _fakeK8sClient.Reset();
    }

    public async Task DisposeAsync()
    {
        await Replica1.DisposeAsync();
        await Replica2.DisposeAsync();
    }
}
