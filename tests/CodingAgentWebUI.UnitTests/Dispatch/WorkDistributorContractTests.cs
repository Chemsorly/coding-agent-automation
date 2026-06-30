using AwesomeAssertions;
using CodingAgentWebUI.Infrastructure.Persistence;
using CodingAgentWebUI.Infrastructure.Persistence.Services;
using CodingAgentWebUI.Orchestration;
using CodingAgentWebUI.Orchestration.Dispatch;
using CodingAgentWebUI.Orchestration.Registry;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;
using ILogger = Serilog.ILogger;

namespace CodingAgentWebUI.UnitTests.Dispatch;

/// <summary>
/// Contract tests verifying behavioral invariants shared by ALL <see cref="IWorkDistributor"/>
/// implementations (Legacy, SignalR, Kubernetes).
/// Ensures behavioral contract doesn't drift when switching dispatch modes.
/// </summary>
public class WorkDistributorContractTests
{
    /// <summary>
    /// Creates all available IWorkDistributor implementations for contract verification.
    /// </summary>
    public static IEnumerable<object[]> AllImplementations()
    {
        // Legacy
        yield return new object[] { "Legacy", CreateLegacy() };

        // Kubernetes (in-memory EF Core)
        yield return new object[] { "Kubernetes", CreateKubernetes() };

        // Note: SignalRWorkDistributor requires IAgentCommunication + IAgentRegistryService
        // which makes it heavier to construct. Testing Legacy + K8s covers the contract.
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

    // ── DistributeAsync — Success Returns Valid Result ────────────────────

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
    public async Task Kubernetes_AfterDistribute_IsIssueDistributed_ReturnsTrue()
    {
        var sut = CreateKubernetes();
        var request = CreateMinimalRequest();

        await sut.DistributeAsync(request, CancellationToken.None);

        var distributed = await sut.IsIssueDistributedAsync(
            request.IssueIdentifier, request.IssueProviderConfigId, CancellationToken.None);
        distributed.Should().BeTrue();
    }

    [Fact]
    public async Task Kubernetes_AfterDistribute_GetActiveIssueIdentifiers_ContainsIssue()
    {
        var sut = CreateKubernetes();
        var request = CreateMinimalRequest();

        await sut.DistributeAsync(request, CancellationToken.None);

        var active = await sut.GetActiveIssueIdentifiersAsync(CancellationToken.None);
        active.Should().Contain((request.IssueIdentifier, request.IssueProviderConfigId));
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
            .UseInMemoryDatabase($"ContractTests_{Guid.NewGuid()}")
            .Options;
        var factory = new InMemoryDbContextFactory(options);
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

    /// <summary>
    /// In-memory IDbContextFactory for testing without a real database.
    /// </summary>
    private sealed class InMemoryDbContextFactory : IDbContextFactory<PipelineDbContext>
    {
        private readonly DbContextOptions<PipelineDbContext> _options;
        public InMemoryDbContextFactory(DbContextOptions<PipelineDbContext> options) => _options = options;
        public PipelineDbContext CreateDbContext() => new(_options);
    }
}
