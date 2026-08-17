using AwesomeAssertions;
using CodingAgentWebUI.Infrastructure.Persistence;
using CodingAgentWebUI.Infrastructure.Persistence.Services;
using CodingAgentWebUI.Orchestration.Dispatch;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;

namespace CodingAgentWebUI.UnitTests.Dispatch;

// ═══════════════════════════════════════════════════════════════════════════════
// Abstract contract base — shared behavioral tests for ALL IWorkDistributor impls
// ═══════════════════════════════════════════════════════════════════════════════

/// <summary>
/// Abstract contract tests verifying behavioral invariants shared by ALL <see cref="IWorkDistributor"/>
/// implementations (Kubernetes).
/// Ensures behavioral contract doesn't drift when switching dispatch modes.
/// </summary>
/// <remarks>
/// The shared contract covers only methods with consistent semantics across all implementations:
/// <list type="bullet">
///   <item><see cref="IWorkDistributor.DistributeAsync"/> success path</item>
///   <item><see cref="IWorkDistributor.IsIssueDistributedAsync"/> post-distribute</item>
///   <item><see cref="IWorkDistributor.GetActiveIssueIdentifiersAsync"/> post-distribute</item>
/// </list>
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

    public virtual void Dispose()
    {
        GC.SuppressFinalize(this);
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
// Additional tests — cold-state + Kubernetes-specific
// ═══════════════════════════════════════════════════════════════════════════════

/// <summary>
/// Additional contract tests covering "cold state" behaviors (no prior distribution)
/// and Kubernetes-specific behaviors not part of the shared contract.
/// </summary>
public class WorkDistributorAdditionalTests
{
    /// <summary>
    /// Creates implementations for cold-state contract verification.
    /// </summary>
    public static TheoryData<string, IWorkDistributor> AllImplementations()
    {
        var data = new TheoryData<string, IWorkDistributor>();
        data.Add("Kubernetes", CreateKubernetes());
        return data;
    }

    // ── Null Request Guard ───────────────────────────────────────────────

    [Theory]
    [MemberData(nameof(AllImplementations))]
    public async Task DistributeAsync_NullRequest_ThrowsArgumentNullException(string implName, IWorkDistributor sut)
    {
        _ = implName;
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
    public void KubernetesWorkDistributor_RequiresConnectedAgents_ReturnsFalse()
    {
        var sut = CreateKubernetes();
        sut.RequiresConnectedAgents.Should().BeFalse();
    }

    // ── Helpers ──────────────────────────────────────────────────────────

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
/// Simple IDbContextFactory for Kubernetes contract tests — uses plain <see cref="PipelineDbContext"/>.
/// </summary>
file sealed class ContractTestSimpleDbContextFactory : IDbContextFactory<PipelineDbContext>
{
    private readonly DbContextOptions<PipelineDbContext> _options;

    public ContractTestSimpleDbContextFactory(DbContextOptions<PipelineDbContext> options)
        => _options = options;

    public PipelineDbContext CreateDbContext() => new(_options);
}
