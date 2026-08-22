using AwesomeAssertions;
using CodingAgentWebUI.Infrastructure.Persistence;
using CodingAgentWebUI.Infrastructure.Persistence.Services;
using CodingAgentWebUI.Orchestration.Dispatch;
using CodingAgentWebUI.Pipeline.LeaderElection;
using CodingAgentWebUI.Pipeline.Interfaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;

namespace CodingAgentWebUI.Orchestration.UnitTests.Dispatch;

/// <summary>
/// Tests for the new <see cref="ReconciliationServiceDependencies"/> parameter-object record
/// and the refactored <see cref="ReconciliationService"/> constructor introduced in PR #1778 (S107 fix).
/// </summary>
public class ReconciliationServiceDependenciesTests
{
    // ── Record construction ──────────────────────────────────────────────

    [Fact]
    public void ReconciliationServiceDependencies_AllRequired_Constructs()
    {
        var deps = CreateValidDeps();

        deps.DbFactory.Should().NotBeNull();
        deps.LeaderElection.Should().NotBeNull();
        deps.KubeClient.Should().NotBeNull();
        deps.TransitionService.Should().NotBeNull();
        deps.Configuration.Should().NotBeNull();
    }

    [Fact]
    public void ReconciliationServiceDependencies_OptionalMembers_DefaultToNull()
    {
        var deps = CreateValidDeps();

        deps.LabelService.Should().BeNull();
        deps.LifecycleManager.Should().BeNull();
        deps.ConsolidationService.Should().BeNull();
        deps.ConfigStore.Should().BeNull();
        deps.DedupGuard.Should().BeNull();
    }

    [Fact]
    public void ReconciliationServiceDependencies_WithOptionalMembers_Stores()
    {
        var labelService = Mock.Of<ILabelService>();
        var lifecycleManager = Mock.Of<IRunLifecycleManager>();
        var consolidationService = Mock.Of<IConsolidationService>();
        var configStore = Mock.Of<IConfigurationStore>();
        var dedupGuard = Mock.Of<IJobDeduplicationGuard>();

        var deps = CreateValidDeps() with
        {
            LabelService = labelService,
            LifecycleManager = lifecycleManager,
            ConsolidationService = consolidationService,
            ConfigStore = configStore,
            DedupGuard = dedupGuard
        };

        deps.LabelService.Should().BeSameAs(labelService);
        deps.LifecycleManager.Should().BeSameAs(lifecycleManager);
        deps.ConsolidationService.Should().BeSameAs(consolidationService);
        deps.ConfigStore.Should().BeSameAs(configStore);
        deps.DedupGuard.Should().BeSameAs(dedupGuard);
    }

    [Fact]
    public void ReconciliationServiceDependencies_IsRecord_SupportsEquality()
    {
        var deps1 = CreateValidDeps();
        var deps2 = deps1 with { };

        deps1.Should().Be(deps2, "records with identical values are equal");
    }

    // ── ReconciliationService constructor ────────────────────────────────

    [Fact]
    public void ReconciliationService_Constructor_AcceptsDeps()
    {
        var deps = CreateValidDeps();
        var act = () => new ReconciliationService(deps);
        act.Should().NotThrow();
    }

    [Fact]
    public void ReconciliationService_Constructor_BindsNamespaceFromConfig()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["WorkDistribution:Namespace"] = "my-namespace"
            })
            .Build();

        var deps = CreateValidDeps() with { Configuration = config };
        // Constructor should not throw even with a custom namespace
        var act = () => new ReconciliationService(deps);
        act.Should().NotThrow();
    }

    [Fact]
    public void ReconciliationService_Constructor_UsesDefaultNamespace_WhenNotConfigured()
    {
        // No namespace in config — should fall back to env var or "default"
        var deps = CreateValidDeps();
        var act = () => new ReconciliationService(deps);
        act.Should().NotThrow();
    }

    [Fact]
    public void ReconciliationService_Constructor_BindsReconciliationOptions()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["WorkDistribution:Reconciliation:PollIntervalSeconds"] = "42",
                ["WorkDistribution:Reconciliation:TimeoutMinutes"] = "30"
            })
            .Build();

        var deps = CreateValidDeps() with { Configuration = config };
        // Should not throw — config binding is best-effort
        var act = () => new ReconciliationService(deps);
        act.Should().NotThrow();
    }

    [Fact]
    public void ReconciliationService_NullDeps_ThrowsArgumentNullException()
    {
        var act = () => new ReconciliationService(null!);
        act.Should().Throw<Exception>("null deps must be rejected");
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    private static ReconciliationServiceDependencies CreateValidDeps()
    {
        var dbFactory = new Mock<IDbContextFactory<PipelineDbContext>>();
        var leaderElection = new Mock<ILeaderElectionService>();
        var kubeClient = new Mock<k8s.IKubernetes>();
        var configuration = new ConfigurationBuilder().Build();
        var transitionService = new WorkItemTransitionService(
            dbFactory.Object,
            Mock.Of<ILogger<WorkItemTransitionService>>());

        return new ReconciliationServiceDependencies(
            DbFactory: dbFactory.Object,
            LeaderElection: leaderElection.Object,
            KubeClient: kubeClient.Object,
            TransitionService: transitionService,
            Configuration: configuration);
    }

    private sealed class TestDbContextFactory(DbContextOptions<PipelineDbContext> options)
        : IDbContextFactory<PipelineDbContext>
    {
        public PipelineDbContext CreateDbContext() => new(options);
        public Task<PipelineDbContext> CreateDbContextAsync(CancellationToken ct = default)
            => Task.FromResult(new PipelineDbContext(options));
    }
}
