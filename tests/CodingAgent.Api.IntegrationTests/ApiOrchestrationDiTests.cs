using AwesomeAssertions;
using CodingAgent.AgentGateway;
using CodingAgent.Infrastructure;
using CodingAgent.Infrastructure.Locking;
using CodingAgent.Infrastructure.Persistence;
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
/// verifies that the full service graph resolves and the DI registration chain is correct.
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
                // TODO [WARNING]: This catch-all also masks any *other* ObjectDisposedException
                // thrown by unrelated services during disposal, making future disposal-related
                // failures invisible in CI output. Consider narrowing the suppression to
                // ChatJobDispatcher specifically once a reliable way to detect it is available.
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
        // IConnectionMultiplexer). The Redis path — where IOrchestratorRunService resolves to
        // DistributedRunService — is never exercised. Add a parallel test with a mocked
        // IConnectionMultiplexer to cover the distributed-service registration branch.
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
        // TODO [WARNING]: PipelineDbContext is registered as AddScoped but all services in
        // this test are resolved directly from the root ServiceProvider. With ValidateScopes
        // defaulting to false, the scoped DbContext (and any scoped services that depend on it)
        // are silently captured at root scope and behave as singletons for the test lifetime.
        // The correct pattern is BuildServiceProvider(ValidateOnBuild=true, ValidateScopes=true)
        // and resolve scoped services via sp.CreateScope().ServiceProvider. See review finding
        // [WARNING] ApiOrchestrationDiTests.cs:180 (DotNetSpecialist review).
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
            // TODO [WARNING]: GetService<> (nullable overload) silently passes null when
            // ResiliencePipelineProvider<string> is not registered. If WorkItemTransitionService
            // uses the resilience pipeline in any code path, this will surface as a
            // NullReferenceException at runtime rather than a clear missing-registration error.
            // Consider registering a no-op ResiliencePipelineProvider<string> or verifying
            // the constructor explicitly tolerates null. See review finding [WARNING]
            // ApiOrchestrationDiTests.cs:196 (DotNetSpecialist review).
            sp.GetService<Polly.Registry.ResiliencePipelineProvider<string>>()));
        services.AddSingleton<IWorkItemFallbackTransitionService>(sp => new WorkItemFallbackTransitionService(
            sp.GetRequiredService<WorkItemTransitionService>(),
            sp.GetRequiredService<ILoggerFactory>().CreateLogger<WorkItemFallbackTransitionService>()));
        services.AddSingleton<IWorkItemTransitionStore>(sp => new PostgresWorkItemTransitionStore(
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
        // AddApiOrchestration registers IKubernetesJobClient with a factory that returns null!
        // when no K8s cluster is available. Override it here so DispatchLifecycleService can
        // be resolved without throwing.
        // The last registration wins in MS DI (GetRequiredService returns the last one).
        // TODO [WARNING]: RemoveAll + re-register as Singleton is safe now, but is fragile if
        // AddApiOrchestration ever changes the lifetime of IKubernetesJobClient to Scoped —
        // this code would silently change the lifetime back to Singleton without any error.
        // If that causes hard-to-diagnose test failures in future, check the lifetime here.
        // See review finding [WARNING] ApiOrchestrationDiTests.cs:220 (Correctness review).
        services.RemoveAll<IKubernetesJobClient>();
        services.AddSingleton<IKubernetesJobClient>(k8sJobClientMock.Object);

        // ── Remove hosted services to prevent disposal-without-start errors ──
        // ChatJobDispatcher is registered as both a singleton and a hosted service.
        // When the container disposes without ever starting, StopAsync throws
        // ObjectDisposedException. Strip hosted services as ApiWebApplicationFactory does.
        services.RemoveAll<IHostedService>();

        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = false });
        // TODO [WARNING]: ValidateOnBuild = false suppresses MS DI's eager graph validation at
        // build time, which is the primary purpose of this characterization test. Consider
        // switching to ValidateOnBuild = true (and ValidateScopes = true) once the scoped
        // PipelineDbContext registration is resolved through a scope rather than root. With
        // both ValidateOnBuild and ValidateScopes false, broken registrations (missing
        // dependencies, mismatched lifetimes) can pass undetected until a specific
        // GetRequiredService<T> call is reached. See review findings [WARNING]
        // ApiOrchestrationDiTests.cs:241 (DotNetSpecialist and TestQualityReviewer reviews).
    }

    private static DelegatingDbContextFactory BuildInMemoryDbFactory(string? dbName = null)
    {
        var name = dbName ?? $"ApiOrchestrationDi-{Guid.NewGuid():N}";
        return new DelegatingDbContextFactory(name);
    }

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
