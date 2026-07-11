using AwesomeAssertions;
using CodingAgentWebUI.Infrastructure.Persistence;
using CodingAgentWebUI.Infrastructure.Persistence.Services;
using CodingAgentWebUI.Orchestration;
using CodingAgentWebUI.Orchestration.Dispatch;
using CodingAgentWebUI.Orchestration.Registry;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using ILogger = Serilog.ILogger;

namespace CodingAgentWebUI.UnitTests.Dispatch;

// ═══════════════════════════════════════════════════════════════════════════════
// Abstract contract base — shared behavioral tests for ALL IWorkDistributor impls
// ═══════════════════════════════════════════════════════════════════════════════

/// <summary>
/// Abstract contract tests verifying behavioral invariants shared by ALL <see cref="IWorkDistributor"/>
/// implementations (Legacy, SignalR, Kubernetes).
/// Ensures behavioral contract doesn't drift when switching dispatch modes.
/// </summary>
/// <remarks>
/// The shared contract covers only methods with consistent semantics across all 3 implementations:
/// <list type="bullet">
///   <item><see cref="IWorkDistributor.DistributeAsync"/> success path</item>
///   <item><see cref="IWorkDistributor.IsIssueDistributedAsync"/> post-distribute</item>
///   <item><see cref="IWorkDistributor.GetActiveIssueIdentifiersAsync"/> post-distribute</item>
/// </list>
/// <c>CancelJobAsync</c> and <c>GetJobStatusAsync</c> are excluded — Legacy returns Unknown/false by design.
/// </remarks>
public abstract class WorkDistributorContractTests : IDisposable
{
    protected abstract IWorkDistributor CreateSut();
    protected abstract void SetupForDistribution(JobDistributionRequest request);

    protected static JobDistributionRequest CreateMinimalRequest() => new()
    {
        IssueIdentifier = "org/repo#42",
        IssueProviderConfigId = "ip-1",
        RepoProviderConfigId = "rp-1",
        InitiatedBy = "contract-test",
        TaskType = WorkItemTaskType.Implementation,
        AgentSelector = "default",
        TimeoutSeconds = 3600
    };

    // ── Shared Contract: DistributeAsync success ─────────────────────────

    [Fact]
    public async Task DistributeAsync_Success_ReturnsSuccessResult()
    {
        var sut = CreateSut();
        var request = CreateMinimalRequest();
        SetupForDistribution(request);

        var result = await sut.DistributeAsync(request, CancellationToken.None);

        result.Success.Should().BeTrue();
    }

    // ── Shared Contract: IsIssueDistributedAsync post-distribute ─────────

    [Fact]
    public async Task AfterDistribute_IsIssueDistributed_ReturnsTrue()
    {
        var sut = CreateSut();
        var request = CreateMinimalRequest();
        SetupForDistribution(request);

        await sut.DistributeAsync(request, CancellationToken.None);

        var distributed = await sut.IsIssueDistributedAsync(
            request.IssueIdentifier, request.IssueProviderConfigId, CancellationToken.None);
        distributed.Should().BeTrue();
    }

    // ── Shared Contract: GetActiveIssueIdentifiersAsync post-distribute ──

    [Fact]
    public async Task AfterDistribute_GetActiveIssueIdentifiers_ContainsIssue()
    {
        var sut = CreateSut();
        var request = CreateMinimalRequest();
        SetupForDistribution(request);

        await sut.DistributeAsync(request, CancellationToken.None);

        var active = await sut.GetActiveIssueIdentifiersAsync(CancellationToken.None);
        active.Should().Contain((request.IssueIdentifier, request.IssueProviderConfigId));
    }

    public virtual void Dispose() { }
}

// ═══════════════════════════════════════════════════════════════════════════════
// Legacy implementation — mocked IJobDispatcher with AgentRegistryService
// ═══════════════════════════════════════════════════════════════════════════════

/// <summary>
/// Runs the shared contract tests against <see cref="LegacyWorkDistributor"/>.
/// Pre-registers an idle agent via <see cref="AgentRegistryService"/> and uses a stateful
/// <see cref="IJobDispatcher"/> mock that tracks distributed issues.
/// </summary>
public class LegacyWorkDistributorContractTests : WorkDistributorContractTests
{
    private readonly Mock<IJobDispatcher> _mockJobDispatcher = new();
    private readonly Mock<IOrchestratorRunService> _mockRunService = new();
    private readonly AgentRegistryService _registry;
    private readonly JobDispatcherService _dispatcherService;
    private readonly HashSet<(string IssueIdentifier, string IssueProviderConfigId)> _distributedIssues = new();

    public LegacyWorkDistributorContractTests()
    {
        var logger = Mock.Of<ILogger>();

        // Pre-register an idle agent via AgentRegistryService
        _registry = new AgentRegistryService(logger);
        _registry.Register(
            new AgentRegistrationMessage
            {
                AgentId = "agent-contract-1",
                Hostname = "contract-test-host",
                Labels = ["default"]
            },
            connectionId: "conn-contract-1");

        _dispatcherService = new JobDispatcherService(_registry, logger);

        // Stateful mock: TryDispatchAsync records the issue, IsIssueBeingProcessedOrQueued checks recorded set
        // TODO: TryDispatchAsync unconditionally returns true — this test asserts mock return value rather than
        // validating request data flows correctly or correct overload is called. Consider stricter argument matching.
        _mockJobDispatcher
            .Setup(d => d.TryDispatchAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string>(),
                It.IsAny<CancellationToken>(), It.IsAny<string?>(), It.IsAny<PipelineProject?>()))
            .ReturnsAsync(true)
            .Callback<string, string, string, string?, string?, string, CancellationToken, string?, PipelineProject?>(
                (issueId, provId, _, _, _, _, _, _, _) => _distributedIssues.Add((issueId, provId)));

        // TODO: IsIssueBeingProcessedOrQueued uses It.IsAny<string>() matchers — incorrect argument
        // forwarding by LegacyWorkDistributor would go undetected. Consider matching specific values.
        _mockJobDispatcher
            .Setup(d => d.IsIssueBeingProcessedOrQueued(It.IsAny<string>(), It.IsAny<string>()))
            .Returns<string, string>((issueId, provId) => _distributedIssues.Contains((issueId, provId)));

        _mockRunService.Setup(r => r.GetActiveRuns()).Returns(new List<PipelineRun>());
    }

    protected override IWorkDistributor CreateSut()
    {
        return new LegacyWorkDistributor(
            _mockJobDispatcher.Object,
            _dispatcherService,
            _mockRunService.Object,
            Mock.Of<ILogger>());
    }

    protected override void SetupForDistribution(JobDistributionRequest request)
    {
        // TODO: This pre-configures GetActiveRuns() BEFORE DistributeAsync is called, meaning
        // AfterDistribute_GetActiveIssueIdentifiers_ContainsIssue would pass even if DistributeAsync
        // were never invoked. Consider verifying empty state before distribution to strengthen the assertion.
        // Configure the run service mock to return the expected issue after dispatch.
        // GetActiveIssueIdentifiersAsync reads from _dispatcherService.GetQueuedJobs() + _runService.GetActiveRuns().
        // Since the mocked TryDispatchAsync doesn't actually enqueue, we provide the state via GetActiveRuns.
        _mockRunService.Setup(r => r.GetActiveRuns()).Returns(new List<PipelineRun>
        {
            PipelineRun.Create(
                runId: Guid.NewGuid().ToString(),
                issueIdentifier: request.IssueIdentifier,
                issueTitle: "Contract test issue",
                issueProviderConfigId: request.IssueProviderConfigId,
                repoProviderConfigId: request.RepoProviderConfigId,
                initiatedBy: request.InitiatedBy)
        });
    }
}

// ═══════════════════════════════════════════════════════════════════════════════
// SignalR implementation — InMemory EF + mocked agent resolver
// ═══════════════════════════════════════════════════════════════════════════════

/// <summary>
/// Runs the shared contract tests against <see cref="SignalRWorkDistributor"/>.
/// Uses InMemory EF Core with mocked agent communication.
/// </summary>
public class SignalRWorkDistributorContractTests : WorkDistributorContractTests
{
    private readonly DbContextOptions<PipelineDbContext> _dbOptions;
    private readonly Mock<IAgentCommunication> _mockAgentComm = new();
    private readonly Mock<ISignalRWorkDistributorAgentResolver> _mockResolver = new();
    private readonly IDbContextFactory<PipelineDbContext> _dbFactory;
    private readonly SignalRWorkDistributor _sut;

    public SignalRWorkDistributorContractTests()
    {
        _dbOptions = new DbContextOptionsBuilder<PipelineDbContext>()
            .UseInMemoryDatabase($"SignalRContract_{Guid.NewGuid()}")
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;

        using (var ctx = new ContractTestPipelineDbContext(_dbOptions))
        {
            ctx.Database.EnsureCreated();
        }

        _dbFactory = new ContractTestDbContextFactory(_dbOptions);

        var transitionService = new WorkItemTransitionService(
            _dbFactory, NullLogger<WorkItemTransitionService>.Instance);

        _sut = new SignalRWorkDistributor(
            _dbFactory,
            _mockAgentComm.Object,
            transitionService,
            _mockResolver.Object,
            new Mock<IOrchestratorRunService>().Object,
            new Mock<IProjectStore>().Object,
            new Mock<ILabelSwapper>().Object,
            NullLogger<SignalRWorkDistributor>.Instance);
    }

    protected override IWorkDistributor CreateSut() => _sut;

    [Fact]
    public void RequiresConnectedAgents_ReturnsFalse()
    {
        _sut.RequiresConnectedAgents.Should().BeFalse();
    }

    protected override void SetupForDistribution(JobDistributionRequest request)
    {
        // Mock agent resolution to return a valid agent
        _mockResolver
            .Setup(r => r.ResolveAgent(It.IsAny<string>()))
            .Returns(new AgentResolveResult("conn-contract-1", "agent-contract-1"));

        // Mock SignalR push to succeed
        _mockAgentComm
            .Setup(c => c.AssignJobAsync(
                It.IsAny<string>(), It.IsAny<JobAssignmentMessage>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
    }

    public override void Dispose()
    {
        using var db = new ContractTestPipelineDbContext(_dbOptions);
        db.Database.EnsureDeleted();
    }
}

// ═══════════════════════════════════════════════════════════════════════════════
// Kubernetes implementation — InMemory EF (simplest)
// ═══════════════════════════════════════════════════════════════════════════════

/// <summary>
/// Runs the shared contract tests against <see cref="KubernetesWorkDistributor"/>.
/// Uses InMemory EF Core — no special setup needed (DistributeAsync is a pure DB insert).
/// </summary>
public class KubernetesWorkDistributorContractTests : WorkDistributorContractTests
{
    private readonly DbContextOptions<PipelineDbContext> _dbOptions;
    private readonly KubernetesWorkDistributor _sut;

    // TODO: Unlike SignalRWorkDistributorContractTests, this class does not override Dispose() to call
    // EnsureDeleted(). While InMemory databases with unique GUID names won't leak across tests, this is
    // inconsistent with the cleanup pattern and could accumulate InMemory database instances in long test runs.
    public KubernetesWorkDistributorContractTests()
    {
        _dbOptions = new DbContextOptionsBuilder<PipelineDbContext>()
            .UseInMemoryDatabase($"K8sContract_{Guid.NewGuid()}")
            .Options;
        var factory = new ContractTestSimpleDbContextFactory(_dbOptions);
        var transitionService = new WorkItemTransitionService(factory, Mock.Of<ILogger<WorkItemTransitionService>>());
        _sut = new KubernetesWorkDistributor(factory, transitionService, Mock.Of<ILogger<KubernetesWorkDistributor>>());
    }

    protected override IWorkDistributor CreateSut() => _sut;

    protected override void SetupForDistribution(JobDistributionRequest request)
    {
        // No-op — K8s DistributeAsync always succeeds (pure DB insert as Pending)
    }
}

// ═══════════════════════════════════════════════════════════════════════════════
// Additional tests — preserved Theory+MemberData tests + Kubernetes-specific
// ═══════════════════════════════════════════════════════════════════════════════

/// <summary>
/// Additional contract tests covering "cold state" behaviors (no prior distribution)
/// and implementation-specific behaviors not part of the shared contract.
/// </summary>
public class WorkDistributorAdditionalTests
{
    /// <summary>
    /// Creates implementations for cold-state contract verification.
    /// </summary>
    public static IEnumerable<object[]> AllImplementations()
    {
        // Legacy
        yield return new object[] { "Legacy", CreateLegacy() };

        // Kubernetes (in-memory EF Core)
        yield return new object[] { "Kubernetes", CreateKubernetes() };
    }

    // ── Null Request Guard ───────────────────────────────────────────────

    [Theory]
    [MemberData(nameof(AllImplementations))]
    public async Task DistributeAsync_NullRequest_ThrowsArgumentNullException(string implName, IWorkDistributor sut)
    {
        _ = implName; // Used for test name disambiguation
        var act = () => sut.DistributeAsync(null!, CancellationToken.None);
        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    // ── GetJobStatusAsync — Unknown for Nonexistent ──────────────────────

    [Theory]
    [MemberData(nameof(AllImplementations))]
    public async Task GetJobStatusAsync_NonexistentId_ReturnsUnknown(string implName, IWorkDistributor sut)
    {
        _ = implName;
        var status = await sut.GetJobStatusAsync("nonexistent-id-12345", CancellationToken.None);
        status.Should().Be(JobDistributionStatus.Unknown);
    }

    // ── IsIssueDistributedAsync — False for Non-Distributed ──────────────

    [Theory]
    [MemberData(nameof(AllImplementations))]
    public async Task IsIssueDistributed_NoActiveItems_ReturnsFalse(string implName, IWorkDistributor sut)
    {
        _ = implName;
        var result = await sut.IsIssueDistributedAsync("org/repo#999", "provider-x", CancellationToken.None);
        result.Should().BeFalse();
    }

    // ── GetActiveIssueIdentifiersAsync — Empty When No Work ──────────────

    [Theory]
    [MemberData(nameof(AllImplementations))]
    public async Task GetActiveIssueIdentifiers_NoItems_ReturnsEmptySet(string implName, IWorkDistributor sut)
    {
        _ = implName;
        var result = await sut.GetActiveIssueIdentifiersAsync(CancellationToken.None);
        result.Should().BeEmpty();
    }

    // ── CancelJobAsync — False for Nonexistent ───────────────────────────

    [Theory]
    [MemberData(nameof(AllImplementations))]
    public async Task CancelJobAsync_NonexistentId_ReturnsFalse(string implName, IWorkDistributor sut)
    {
        _ = implName;
        var result = await sut.CancelJobAsync("nonexistent-job-id", CancellationToken.None);
        result.Should().BeFalse();
    }

    // ── ReconcileStuckItemsAsync — Zero When Clean ───────────────────────

    [Theory]
    [MemberData(nameof(AllImplementations))]
    public async Task ReconcileStuckItems_NoItems_ReturnsZero(string implName, IWorkDistributor sut)
    {
        _ = implName;
        var count = await sut.ReconcileStuckItemsAsync(CancellationToken.None);
        count.Should().Be(0);
    }

    // ── Kubernetes-specific: GetJobStatus + Cancel (not part of shared contract) ──

    [Fact]
    public async Task Kubernetes_DistributeAsync_Success_ReturnsWorkItemId()
    {
        var sut = CreateKubernetes();
        var request = CreateMinimalRequest();

        var result = await sut.DistributeAsync(request, CancellationToken.None);

        result.Success.Should().BeTrue();
        result.WorkItemId.Should().NotBeNullOrEmpty();
        result.ErrorMessage.Should().BeNull();
    }

    [Fact]
    public async Task Kubernetes_AfterDistribute_GetJobStatus_ReturnsPending()
    {
        var sut = CreateKubernetes();
        var request = CreateMinimalRequest();

        var result = await sut.DistributeAsync(request, CancellationToken.None);

        var status = await sut.GetJobStatusAsync(result.WorkItemId!, CancellationToken.None);
        status.Should().Be(JobDistributionStatus.Pending);
    }

    [Fact]
    public async Task Kubernetes_AfterDistributeAndCancel_GetJobStatus_ReturnsCancelled()
    {
        var sut = CreateKubernetes();
        var request = CreateMinimalRequest();

        var result = await sut.DistributeAsync(request, CancellationToken.None);
        var cancelled = await sut.CancelJobAsync(result.WorkItemId!, CancellationToken.None);

        cancelled.Should().BeTrue();
        var status = await sut.GetJobStatusAsync(result.WorkItemId!, CancellationToken.None);
        status.Should().Be(JobDistributionStatus.Cancelled);
    }

    // ── RequiresConnectedAgents — Property Value Verification ───────────

    [Fact]
    public void LegacyWorkDistributor_RequiresConnectedAgents_ReturnsTrue()
    {
        var sut = CreateLegacy();
        sut.RequiresConnectedAgents.Should().BeTrue();
    }

    [Fact]
    public void KubernetesWorkDistributor_RequiresConnectedAgents_ReturnsFalse()
    {
        var sut = CreateKubernetes();
        sut.RequiresConnectedAgents.Should().BeFalse();
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    private static IWorkDistributor CreateLegacy()
    {
        var mockJobDispatcher = new Mock<IJobDispatcher>();
        mockJobDispatcher.Setup(d => d.IsIssueBeingProcessedOrQueued(It.IsAny<string>(), It.IsAny<string>()))
            .Returns(false);
        var logger = Mock.Of<ILogger>();
        var registry = new AgentRegistryService(logger);
        var dispatcherService = new JobDispatcherService(registry, logger);
        var mockRunService = new Mock<IOrchestratorRunService>();
        mockRunService.Setup(r => r.GetActiveRuns()).Returns(new List<PipelineRun>());

        return new LegacyWorkDistributor(
            mockJobDispatcher.Object,
            dispatcherService,
            mockRunService.Object,
            logger);
    }

    private static KubernetesWorkDistributor CreateKubernetes()
    {
        var options = new DbContextOptionsBuilder<PipelineDbContext>()
            .UseInMemoryDatabase($"AdditionalTests_{Guid.NewGuid()}")
            .Options;
        var factory = new ContractTestSimpleDbContextFactory(options);
        var transitionService = new WorkItemTransitionService(factory, Mock.Of<ILogger<WorkItemTransitionService>>());
        return new KubernetesWorkDistributor(factory, transitionService, Mock.Of<ILogger<KubernetesWorkDistributor>>());
    }

    private static JobDistributionRequest CreateMinimalRequest() => new()
    {
        IssueIdentifier = "org/repo#42",
        IssueProviderConfigId = "ip-1",
        RepoProviderConfigId = "rp-1",
        InitiatedBy = "contract-test",
        TaskType = WorkItemTaskType.Implementation,
        AgentSelector = "default",
        TimeoutSeconds = 3600
    };
}

// ═══════════════════════════════════════════════════════════════════════════════
// Test infrastructure — file-scoped helpers
// ═══════════════════════════════════════════════════════════════════════════════

/// <summary>
/// InMemory PipelineDbContext that disables RowVersion concurrency tokens and
/// removes filtered partial indexes (not supported by InMemory provider).
/// Required for SignalR tests where DistributeAsync updates work item status.
/// </summary>
file sealed class ContractTestPipelineDbContext : PipelineDbContext
{
    public ContractTestPipelineDbContext(DbContextOptions<PipelineDbContext> options)
        : base(options) { }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        foreach (var entityType in modelBuilder.Model.GetEntityTypes())
        {
            var rowVersionProp = entityType.FindProperty("RowVersion");
            if (rowVersionProp != null)
            {
                rowVersionProp.IsConcurrencyToken = false;
                rowVersionProp.ValueGenerated = Microsoft.EntityFrameworkCore.Metadata.ValueGenerated.Never;
            }
        }

        foreach (var entityType in modelBuilder.Model.GetEntityTypes())
        {
            var indexesToRemove = entityType.GetIndexes()
                .Where(i => i.GetFilter() != null)
                .ToList();
            foreach (var index in indexesToRemove)
            {
                entityType.RemoveIndex(index);
            }
        }
    }
}

/// <summary>
/// IDbContextFactory for SignalR contract tests — uses <see cref="ContractTestPipelineDbContext"/>
/// with explicit <see cref="IDbContextFactory{TContext}.CreateDbContextAsync"/> implementation.
/// </summary>
file sealed class ContractTestDbContextFactory : IDbContextFactory<PipelineDbContext>
{
    private readonly DbContextOptions<PipelineDbContext> _options;

    public ContractTestDbContextFactory(DbContextOptions<PipelineDbContext> options)
        => _options = options;

    public PipelineDbContext CreateDbContext()
        => new ContractTestPipelineDbContext(_options);

    public Task<PipelineDbContext> CreateDbContextAsync(CancellationToken ct = default)
        => Task.FromResult(CreateDbContext());
}

/// <summary>
/// Simple IDbContextFactory for Kubernetes contract tests — uses plain <see cref="PipelineDbContext"/>
/// (K8s DistributeAsync only inserts, no RowVersion usage in the contract test paths).
/// </summary>
file sealed class ContractTestSimpleDbContextFactory : IDbContextFactory<PipelineDbContext>
{
    private readonly DbContextOptions<PipelineDbContext> _options;

    public ContractTestSimpleDbContextFactory(DbContextOptions<PipelineDbContext> options)
        => _options = options;

    public PipelineDbContext CreateDbContext() => new(_options);
}
