using AwesomeAssertions;
using CodingAgent.AgentGateway;
using CodingAgent.Infrastructure;
using CodingAgent.Infrastructure.Locking;
using CodingAgent.Infrastructure.Persistence;
using CodingAgent.Infrastructure.Persistence.Entities;
using CodingAgent.Infrastructure.Persistence.Services;
using CodingAgent.Infrastructure.Persistence.Stores;
using CodingAgent.Kubernetes;
using CodingAgent.Orchestration;
using CodingAgent.Orchestration.Dispatch;
using CodingAgent.Orchestration.Health;
using CodingAgent.Orchestration.Registry;
using CodingAgent.Pipeline;
using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;
using CodingAgent.Pipeline.Services;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Moq;

namespace CodingAgent.Api.IntegrationTests;

/// <summary>
/// Characterization tests for <see cref="ApiServiceCollectionExtensions.AddApiOrchestration"/>:
/// verifies that the full service graph resolves, the extracted helpers behave correctly,
/// and the Redis-fallback helper works as expected.
///
/// These tests do NOT go through <c>ApiWebApplicationFactory</c> (which stubs
/// <c>AssignmentEnricher</c> and strips hosted services), so they are the only tests that
/// verify the real registration chain produced by <c>AddApiOrchestration</c>.
/// </summary>
public sealed class ApiOrchestrationDiTests : IAsyncLifetime
{
    private readonly string _dbName = $"ApiOrchestrationDi-{Guid.NewGuid():N}";
    private ServiceProvider? _provider;

    public async Task InitializeAsync()
    {
        _provider = BuildProvider();
        await Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        if (_provider is not null)
        {
            try
            {
                await _provider.DisposeAsync();
            }
            catch (ObjectDisposedException)
            {
                // ChatJobDispatcher.DisposeAsync calls StopAsync which accesses the
                // internal CancellationTokenSource. If the dispatcher was never started
                // (StartAsync never called), the CTS may already be in an unexpected state.
                // The test body assertions already ran successfully — suppress the noise here.
            }
        }
    }

    // ── Characterization test ─────────────────────────────────────────────────

    /// <summary>
    /// Asserts every service type registered by <see cref="ApiServiceCollectionExtensions.AddApiOrchestration"/>
    /// can be resolved from the resulting <see cref="ServiceProvider"/>.
    ///
    /// This is the sole test that validates the real (non-stubbed) registration chain.
    /// <c>ApiWebApplicationFactory</c> replaces <see cref="AssignmentEnricher"/> with a
    /// passthrough stub, so <c>Program.cs</c>'s startup validation never resolves the real graph.
    /// </summary>
    [Fact]
    public void AddApiOrchestration_ServiceGraph_AllExpectedServicesRegistered()
    {
        var sp = _provider!;

        // TODO [WARNING]: All assertions below use .Should().NotBeNull() which is vacuous —
        // GetRequiredService<T> already throws when the service is unresolvable, so the only
        // real signal is whether the call throws. For services with a branch-dependent concrete
        // type (e.g. IOrchestratorRunService resolves to OrchestratorRunService without Redis,
        // DistributedRunService with Redis), add .Should().BeOfType<OrchestratorRunService>()
        // to actually validate the correct branch was taken.
        // See review finding [WARNING] ApiOrchestrationDiTests.cs:86 (TestQualityReviewer review).

        // TODO [WARNING]: This test validates only the no-Redis path (BuildProvider registers no
        // IConnectionMultiplexer). The Redis path — where ResolveRedisStoreOrNull returns a
        // non-null store and IOrchestratorRunService resolves to DistributedRunService — is never
        // exercised. Acceptance criterion 3 ("services resolvable unchanged") should be verified
        // for both wiring variants. Add a parallel test with a mocked IConnectionMultiplexer to
        // cover the distributed-service registration branch.
        // See review finding [WARNING] ApiOrchestrationDiTests.cs:86 (TestQualityReviewer review).

        // Core orchestration
        sp.GetRequiredService<AgentRegistryService>().Should().NotBeNull();
        sp.GetRequiredService<IAgentRegistryService>().Should().NotBeNull();
        sp.GetRequiredService<OrchestratorRunService>().Should().NotBeNull();
        sp.GetRequiredService<IOrchestratorRunService>().Should().NotBeNull();

        // Token vending
        sp.GetRequiredService<TokenVendingService>().Should().NotBeNull();
        sp.GetRequiredService<ITokenVendingService>().Should().NotBeNull();

        // Label services
        sp.GetRequiredService<ILabelService>().Should().NotBeNull();
        sp.GetRequiredService<ILabelSwapService>().Should().NotBeNull();

        // Lifecycle & consolidation
        sp.GetRequiredService<PipelineRunLifecycleService>().Should().NotBeNull();
        sp.GetRequiredService<IChangeNotifier>().Should().NotBeNull();
        sp.GetRequiredService<IChatNotifier>().Should().NotBeNull();
        sp.GetRequiredService<IConsolidationService>().Should().NotBeNull();
        sp.GetRequiredService<IAgentCommunication>().Should().NotBeNull();
        sp.GetRequiredService<ModelFetchService>().Should().NotBeNull();
        sp.GetRequiredService<ConsolidationBadgeService>().Should().NotBeNull();
        sp.GetRequiredService<IActiveRunQueryService>().Should().NotBeNull();
        sp.GetRequiredService<IRunLifecycleManager>().Should().NotBeNull();
        sp.GetRequiredService<WorkItemStatusTransitionService>().Should().NotBeNull();
        sp.GetRequiredService<IConsolidationJobPreparationService>().Should().NotBeNull();

        // Kubernetes & dispatch infrastructure
        sp.GetRequiredService<IKubernetesJobClient>().Should().NotBeNull();
        sp.GetRequiredService<IJobCleanupStrategy>().Should().NotBeNull();
        sp.GetRequiredService<JobTemplateStore>().Should().NotBeNull();
        sp.GetRequiredService<DatabaseMaintenanceService>().Should().NotBeNull();

        // Dispatch subsystem
        sp.GetRequiredService<ProfileResolver>().Should().NotBeNull();
        sp.GetRequiredService<QualityGateResolver>().Should().NotBeNull();
        sp.GetRequiredService<ReviewerResolver>().Should().NotBeNull();
        sp.GetRequiredService<DispatchResolutionService>().Should().NotBeNull();
        sp.GetRequiredService<DispatchInfrastructure>().Should().NotBeNull();
        sp.GetRequiredService<ConsolidationTemplateResolver>().Should().NotBeNull();
        sp.GetRequiredService<AssignmentEnricher>().Should().NotBeNull();
        sp.GetRequiredService<CodingAgent.Api.Dispatch.DispatchLifecycleService>().Should().NotBeNull();
        sp.GetRequiredService<CodingAgent.Api.Dispatch.DispatchTemplateResolver>().Should().NotBeNull();
        sp.GetRequiredService<CodingAgent.Api.Dispatch.DispatchStateBuilder>().Should().NotBeNull();
        sp.GetRequiredService<CodingAgent.Api.Dispatch.DispatchWorkItemService>().Should().NotBeNull();

        // Chat dispatch
        sp.GetRequiredService<ModelFetchJobService>().Should().NotBeNull();
        sp.GetRequiredService<ChatJobDispatcher>().Should().NotBeNull();
        sp.GetRequiredService<IChatJobDispatcher>().Should().NotBeNull();
    }

    // ── CreateIsIssueDistributedDelegate tests ─────────────────────────────────

    [Fact]
    public async Task CreateIsIssueDistributedDelegate_WhenWorkItemIsActive_ReturnsTrue()
    {
        var dbFactory = BuildInMemoryDbFactory();

        // Seed an active work item
        await using var db = dbFactory.CreateDbContext();
        // TODO [WARNING]: Only WorkItemStatus.Running is seeded. PipelineConstants.ActiveWorkItemStatuses
        // likely contains additional statuses (e.g. Pending, Dispatching, Reviewing). A regression
        // where a non-Running active status is accidentally excluded from ActiveWorkItemStatuses would
        // not be caught here. Add a second variant or parameterise over all ActiveWorkItemStatuses.
        // See review finding [WARNING] ApiOrchestrationDiTests.cs:139 (TestQualityReviewer review).
        db.WorkItems.Add(CreateWorkItem("issue-1", "config-1", WorkItemStatus.Running, null));
        await db.SaveChangesAsync();

        var delegateUnderTest = ApiServiceCollectionExtensions.CreateIsIssueDistributedDelegate(dbFactory);

        var result = await delegateUnderTest("issue-1", "config-1", CancellationToken.None);

        result.Should().BeTrue("a Running work item is active and counts as distributed");
    }

    [Fact]
    public async Task CreateIsIssueDistributedDelegate_WhenWorkItemCompletedRecentlyButTerminal_ReturnsTrue()
    {
        var dbFactory = BuildInMemoryDbFactory();

        // Seed a recently-completed (within cooldown) work item
        // TODO [WARNING]: TimeSpan.FromSeconds(5) is a hard-coded offset. If DefaultRestartDedupCooldown
        // is ever reduced to a value <= 5 seconds, this test will produce a false-negative failure.
        // Use a fraction of PipelineConstants.DefaultRestartDedupCooldown (e.g. cooldown / 2) so the
        // completion timestamp stays within the window regardless of the constant's value.
        // See review finding [WARNING] ApiOrchestrationDiTests.cs:155 (TestQualityReviewer review).
        var recentCompletion = DateTimeOffset.UtcNow - TimeSpan.FromSeconds(5);
        await using var db = dbFactory.CreateDbContext();
        db.WorkItems.Add(CreateWorkItem("issue-2", "config-2", WorkItemStatus.Succeeded, recentCompletion));
        await db.SaveChangesAsync();

        var delegateUnderTest = ApiServiceCollectionExtensions.CreateIsIssueDistributedDelegate(dbFactory);

        var result = await delegateUnderTest("issue-2", "config-2", CancellationToken.None);

        result.Should().BeTrue("a recently-completed work item within the dedup cooldown counts as distributed");
    }

    [Fact]
    public async Task CreateIsIssueDistributedDelegate_WhenNoMatchingWorkItem_ReturnsFalse()
    {
        var dbFactory = BuildInMemoryDbFactory();

        // No work items seeded
        // TODO [WARNING]: This test seeds an empty database. It does not verify that the delegate
        // correctly ignores work items for OTHER issue/config pairs — the cross-contamination case.
        // A query-predicate bug that fails to filter by IssueProviderConfigId would pass this test
        // because the DB is empty. Add a variant that seeds a row for a different
        // issueProviderConfigId (same issueIdentifier) and asserts the delegate still returns false.
        // See review finding [WARNING] ApiOrchestrationDiTests.cs:172 (TestQualityReviewer review).
        var delegateUnderTest = ApiServiceCollectionExtensions.CreateIsIssueDistributedDelegate(dbFactory);

        var result = await delegateUnderTest("issue-3", "config-3", CancellationToken.None);

        result.Should().BeFalse("no work item exists for this issue+config pair");
    }

    [Fact]
    public async Task CreateIsIssueDistributedDelegate_WhenWorkItemCompletedBeyondCooldown_ReturnsFalse()
    {
        var dbFactory = BuildInMemoryDbFactory();

        // Seed a work item that completed long ago (well outside the cooldown window)
        var staleCompletion = DateTimeOffset.UtcNow - PipelineConstants.DefaultRestartDedupCooldown - TimeSpan.FromMinutes(10);
        await using var db = dbFactory.CreateDbContext();
        db.WorkItems.Add(CreateWorkItem("issue-4", "config-4", WorkItemStatus.Succeeded, staleCompletion));
        await db.SaveChangesAsync();

        var delegateUnderTest = ApiServiceCollectionExtensions.CreateIsIssueDistributedDelegate(dbFactory);

        var result = await delegateUnderTest("issue-4", "config-4", CancellationToken.None);

        result.Should().BeFalse("a work item completed beyond the cooldown window does not count as distributed");
    }

    // TODO [WARNING]: There is no test for the exact boundary of the cooldown window — a WorkItem
    // whose CompletedAt equals exactly DateTimeOffset.UtcNow - DefaultRestartDedupCooldown.
    // The production query uses >= since (inclusive lower bound), so a completion precisely at the
    // boundary should return true. Without a boundary test, an off-by-one regression (> instead of
    // >=) would go undetected. This is the primary invariant of the TOCTOU-avoidance logic and the
    // most likely source of a future regression.
    // See review finding [WARNING] ApiOrchestrationDiTests.cs:195 (TestQualityReviewer review).

    // ── ResolveRedisStoreOrNull tests ────────────────────────────────────────────

    [Fact]
    public void ResolveRedisStoreOrNull_WhenNoMultiplexer_ReturnsNull()
    {
        // Provider with no IConnectionMultiplexer registered
        var services = new ServiceCollection();
        using var sp = services.BuildServiceProvider();

        var result = ApiServiceCollectionExtensions.ResolveRedisStoreOrNull(sp);

        result.Should().BeNull("without a registered IConnectionMultiplexer the fallback path returns null");
    }

    // ── Helpers ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// Builds a minimal <see cref="ServiceProvider"/> that wires all infrastructure
    /// prerequisites and calls <see cref="ApiServiceCollectionExtensions.AddApiOrchestration"/>.
    /// Does NOT use the real Postgres stack — all persistence is InMemory.
    /// </summary>
    private ServiceProvider BuildProvider()
    {
        var services = new ServiceCollection();

        // ── Configuration ────────────────────────────────────────────────────
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["WorkDistribution:Dispatch:ChatReplicaCount"] = "1"
            })
            .Build();
        services.AddSingleton<IConfiguration>(config);

        // ── Logging ──────────────────────────────────────────────────────────
        services.AddLogging();
        services.AddSingleton(Serilog.Log.Logger);

        // ── EF Core InMemory ─────────────────────────────────────────────────
        var dbFactory = BuildInMemoryDbFactory(_dbName);
        services.AddSingleton<IDbContextFactory<PipelineDbContext>>(dbFactory);
        services.AddScoped(sp => sp.GetRequiredService<IDbContextFactory<PipelineDbContext>>().CreateDbContext());

        // ── IConfigurationStore (mock) + sub-interface forwarding ────────────
        var configStoreMock = new Mock<IConfigurationStore>();
        services.AddSingleton<IConfigurationStore>(configStoreMock.Object);
        services.RegisterConfigStoreSubInterfaces();

        // ── Persistence services (subset required by AddApiOrchestration) ────
        services.AddSingleton<IPipelineRunHistoryService>(new Mock<IPipelineRunHistoryService>().Object);
        services.AddSingleton<IConsolidationRunStore>(new Mock<IConsolidationRunStore>().Object);
        services.AddSingleton<IHarnessSuggestionStore>(new Mock<IHarnessSuggestionStore>().Object);
        services.AddSingleton<ILoopStateStore>(new Mock<ILoopStateStore>().Object);
        services.AddSingleton<IFeedbackCommentOutbox>(new Mock<IFeedbackCommentOutbox>().Object);

        // ── WorkItemTransitionService + fallback service ─────────────────────
        // These are registered by AddApiInfrastructure; replicate minimally here.
        services.AddSingleton<WorkItemTransitionService>(sp => new WorkItemTransitionService(
            sp.GetRequiredService<IDbContextFactory<PipelineDbContext>>(),
            sp.GetRequiredService<ILoggerFactory>().CreateLogger<WorkItemTransitionService>(),
            sp.GetService<Polly.Registry.ResiliencePipelineProvider<string>>()));
        services.AddSingleton<IWorkItemFallbackTransitionService>(sp => new WorkItemFallbackTransitionService(
            sp.GetRequiredService<WorkItemTransitionService>(),
            sp.GetRequiredService<ILoggerFactory>().CreateLogger<WorkItemFallbackTransitionService>()));
        services.AddSingleton<IWorkItemTransitionStore>(sp => new EfWorkItemTransitionStore(
            sp.GetRequiredService<IDbContextFactory<PipelineDbContext>>(),
            sp.GetRequiredService<WorkItemTransitionService>()));

        // ── Distributed lock provider (in-process for tests) ─────────────────
        services.AddDistributedLockProvider(null);

        // ── TimeProvider ─────────────────────────────────────────────────────
        services.AddSingleton(TimeProvider.System);

        // ── SignalR IHubContext (mock) ────────────────────────────────────────
        var hubContextMock = new Mock<IHubContext<AgentHub, IAgentHubClient>>();
        services.AddSingleton(hubContextMock.Object);

        // ── IKubernetesJobClient (mock) — will override AddApiOrchestration's registration ──
        // Stored here; registered after AddApiOrchestration below so the mock wins.
        var k8sJobClientMock = new Mock<IKubernetesJobClient>();

        // ── HTTP client factory (required by TokenVendingService) ────────────
        services.AddHttpClient();

        // ── AddApiOrchestration — the method under test ──────────────────────
        services.AddApiOrchestration(config);

        // ── Override IKubernetesJobClient with a mock AFTER AddApiOrchestration ──
        // AddApiOrchestration's AddKubernetes sub-method registers IKubernetesJobClient
        // with a factory that returns null! when no K8s cluster is available. Override
        // it here so DispatchLifecycleService can be resolved without throwing.
        // The last registration wins in MS DI (GetRequiredService returns the last one).
        services.RemoveAll<IKubernetesJobClient>();
        services.AddSingleton<IKubernetesJobClient>(k8sJobClientMock.Object);

        // ── Remove hosted services to prevent disposal-without-start errors ──
        // ChatJobDispatcher is registered as both a singleton and a hosted service.
        // When the container disposes without ever starting, StopAsync throws
        // ObjectDisposedException. Strip hosted services as ApiWebApplicationFactory does.
        services.RemoveAll<IHostedService>();

        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = false });
    }

    private static DelegatingDbContextFactory BuildInMemoryDbFactory(string? dbName = null)
    {
        var name = dbName ?? $"ApiOrchestrationDi-{Guid.NewGuid():N}";
        return new DelegatingDbContextFactory(name);
    }

    private static WorkItemEntity CreateWorkItem(
        string issueIdentifier,
        string issueProviderConfigId,
        WorkItemStatus status,
        DateTimeOffset? completedAt)
        => new()
        {
            Id = Guid.NewGuid(),
            TaskType = WorkItemTaskType.Implementation,
            IssueIdentifier = issueIdentifier,
            IssueProviderConfigId = issueProviderConfigId,
            Status = status,
            Payload = null,
            AgentSelector = "test",
            TimeoutSeconds = 1800,
            CreatedAt = DateTimeOffset.UtcNow,
            CompletedAt = completedAt
        };

    // ── Minimal InMemory DbContext factory ───────────────────────────────────────

    private sealed class DelegatingDbContextFactory(string dbName) : IDbContextFactory<PipelineDbContext>
    {
        private readonly string _dbName = dbName;

        public PipelineDbContext CreateDbContext()
        {
            var options = new DbContextOptionsBuilder<PipelineDbContext>()
                .UseInMemoryDatabase(_dbName)
                .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
                .Options;
            return new DiTestPipelineDbContext(options);
        }

        public Task<PipelineDbContext> CreateDbContextAsync(CancellationToken ct = default)
            => Task.FromResult(CreateDbContext());
    }

    private sealed class DiTestPipelineDbContext(DbContextOptions<PipelineDbContext> options) : PipelineDbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);
            // InMemory provider does not support RowVersion concurrency tokens
            foreach (var entityType in modelBuilder.Model.GetEntityTypes())
            {
                var rowVersionProp = entityType.FindProperty("RowVersion");
                if (rowVersionProp != null)
                {
                    rowVersionProp.IsConcurrencyToken = false;
                    rowVersionProp.ValueGenerated = Microsoft.EntityFrameworkCore.Metadata.ValueGenerated.Never;
                }
            }
            // InMemory provider does not support filtered indexes
            foreach (var entityType in modelBuilder.Model.GetEntityTypes())
            {
                var indexesToRemove = entityType.GetIndexes().Where(i => i.GetFilter() != null).ToList();
                foreach (var index in indexesToRemove)
                    entityType.RemoveIndex(index);
            }
        }
    }
}
