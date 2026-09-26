using CodingAgent.AgentGateway;
using CodingAgent.Web.E2ETests.Fakes;
using CodingAgent.Orchestration;
using CodingAgent.Orchestration.Registry;
using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;
using CodingAgent.Web.TestUtilities;
using InMemoryConfigurationStore = CodingAgent.Web.E2ETests.Fakes.InMemoryConfigurationStore;
using Microsoft.Extensions.DependencyInjection;

namespace CodingAgent.Web.E2ETests.Infrastructure;

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
    /// The shared <see cref="FakeProviderFactory"/> used by both replicas.
    /// Assertions against label swaps use <c>FakeProviders.IssueProvider.LabelChanges</c>,
    /// which records every <see cref="IIssueProvider.AddLabelsAsync"/> and
    /// <see cref="IIssueProvider.RemoveLabelAsync"/> call.
    /// </summary>
    public FakeProviderFactory FakeProviders => _fakeProviders;

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

    /// <summary>
    /// The <see cref="IRunLifecycleManager"/> resolved from Replica1.
    /// Used by multi-replica label tests to invoke <see cref="IRunLifecycleManager.FailRunWithLabelAsync"/>
    /// directly (simulating the HTTP <c>Failed</c> POST path) and assert that the correct label is applied.
    /// </summary>
    public IRunLifecycleManager LifecycleManager1 => Replica1.Services.GetRequiredService<IRunLifecycleManager>();

    /// <summary>
    /// The <see cref="IRunLifecycleManager"/> resolved from Replica2.
    /// </summary>
    public IRunLifecycleManager LifecycleManager2 => Replica2.Services.GetRequiredService<IRunLifecycleManager>();

    /// <summary>
    /// The <see cref="IAgentJobLifecycleService"/> resolved from Replica1.
    /// Used by multi-replica label tests to invoke
    /// <see cref="IAgentJobLifecycleService.HandleJobCompletedAsync"/> directly (simulating the hub
    /// <c>ReportJobCompleted</c> path on Replica1) and exercise the full
    /// <see cref="RegularJobCompletionStrategy"/> → <c>skipLabelSwap</c> code path.
    /// </summary>
    public IAgentJobLifecycleService JobLifecycleService1 => Replica1.Services.GetRequiredService<IAgentJobLifecycleService>();

    /// <summary>
    /// The <see cref="IAgentJobLifecycleService"/> resolved from Replica2.
    /// </summary>
    public IAgentJobLifecycleService JobLifecycleService2 => Replica2.Services.GetRequiredService<IAgentJobLifecycleService>();

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

    /// <summary>
    /// Completes a job using the production two-channel path, with explicit control over
    /// which replica receives each channel. This is the multi-replica equivalent of
    /// <see cref="FakeAgentClient.CompleteLikeProductionAsync"/>.
    ///
    /// <para>
    /// In production, a K8s pod may connect its SignalR hub to one replica while an independent
    /// HTTP POST (or a job-controller timeout) is handled by a different replica. This method
    /// allows tests to reproduce that topology.
    /// </para>
    ///
    /// <list type="bullet">
    ///   <item>
    ///     The <paramref name="agent"/> must already be connected (via
    ///     <see cref="FakeAgentClient.ConnectAsync"/>) to the hub replica — this determines which
    ///     replica handles <c>ReportJobCompleted</c>.
    ///   </item>
    ///   <item>
    ///     <paramref name="httpReplica"/> selects which replica's API receives the HTTP POST.
    ///     Pass <see cref="ReplicaSelector.Replica1"/> to target
    ///     <see cref="AgentHubUrl1"/> and <see cref="ReplicaSelector.Replica2"/> to target
    ///     <see cref="AgentHubUrl2"/>. Passing the same replica as the hub connection
    ///     simulates single-replica (same-node) completion.
    ///   </item>
    /// </list>
    /// </summary>
    /// <param name="agent">The fake agent; must be connected before calling this method.</param>
    /// <param name="jobId">The job / work-item ID.</param>
    /// <param name="payload">The completion payload.</param>
    /// <param name="httpReplica">Which replica should receive the HTTP POST.</param>
    /// <param name="order">Controls the sequencing of the HTTP and hub calls.</param>
    /// <param name="ct">Cancellation token.</param>
    public Task CompleteLikeProductionCrossReplicaAsync(
        FakeAgentClient agent,
        string jobId,
        JobCompletionPayload payload,
        ReplicaSelector httpReplica,
        CompletionOrder order = CompletionOrder.HttpThenHub,
        CancellationToken ct = default)
    {
        var httpAddress = httpReplica == ReplicaSelector.Replica1 ? AgentHubUrl1 : AgentHubUrl2;
        return agent.CompleteLikeProductionAsync(jobId, payload, httpAddress, order, ct);
    }

    public async Task DisposeAsync()
    {
        await Replica1.DisposeAsync();
        await Replica2.DisposeAsync();
    }
}
