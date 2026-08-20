using AwesomeAssertions;
using CodingAgentWebUI.Api.Client;
using CodingAgentWebUI.Orchestration.Dispatch;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
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
    public virtual async Task AfterDistribute_IsIssueDistributed_ReturnsTrue()
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
    public virtual async Task AfterDistribute_GetActiveIssueIdentifiers_ContainsIssue()
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
// Kubernetes implementation — backed by an in-memory Pipeline API fake
// ═══════════════════════════════════════════════════════════════════════════════

/// <summary>
/// Runs the shared contract tests against <see cref="KubernetesWorkDistributor"/>.
/// The distributor is a pure Pipeline API client, so the SUT is paired with an in-memory
/// stand-in for the API (see <see cref="ApiWorkItemClientFake"/>) rather than a database.
/// </summary>
public class KubernetesWorkDistributorContractTests : WorkDistributorContractTests
{
    private readonly KubernetesWorkDistributor _sut;

    public KubernetesWorkDistributorContractTests()
    {
        _sut = new KubernetesWorkDistributor(
            ApiWorkItemClientFake.Create(),
            Mock.Of<ILogger<KubernetesWorkDistributor>>());
    }

    protected override IWorkDistributor CreateSut() => _sut;

    protected override void SetupForDistribution(JobDistributionRequest request)
    {
        // No-op — DistributeAsync records the item in the API fake, which then serves
        // the dedup and status reads. No per-test priming needed.
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
        // DistributeAsync creates the WorkItem via the API in Pending state; the Job Controller's
        // dispatch loop is what later moves it to Dispatched/Running.
        var sut = CreateKubernetes();
        var request = CreateMinimalRequest();

        var result = await sut.DistributeAsync(request, CancellationToken.None);

        var status = await sut.GetJobStatusAsync(result.WorkItemId!, CancellationToken.None);
        status.Should().Be(JobDistributionStatus.Pending);
    }

    // ── RequiresConnectedAgents — Property Value Verification ───────────

    [Fact]
    public void KubernetesWorkDistributor_RequiresConnectedAgents_ReturnsFalse()
    {
        IWorkDistributor sut = CreateKubernetes();
        sut.RequiresConnectedAgents.Should().BeFalse();
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    private static KubernetesWorkDistributor CreateKubernetes()
        => new(ApiWorkItemClientFake.Create(), Mock.Of<ILogger<KubernetesWorkDistributor>>());

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
/// In-memory stand-in for the Pipeline API's work-item endpoints, covering the four operations
/// <see cref="KubernetesWorkDistributor"/> uses. Created work items are remembered so that the
/// dedup and status reads answer from them — the same round trip a live API performs, and the
/// only way the distributor's post-distribute contract can be exercised now that it holds no
/// database of its own.
/// </summary>
file static class ApiWorkItemClientFake
{
    public static IPipelineApiWorkItemClient Create()
    {
        var created = new List<(Guid Id, JobDistributionRequest Request)>();
        var mock = new Mock<IPipelineApiWorkItemClient>();

        mock.Setup(c => c.CreateAsync(It.IsAny<JobDistributionRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((JobDistributionRequest request, CancellationToken _) =>
            {
                var id = Guid.NewGuid();
                created.Add((id, request));
                return id;
            });

        // POST /api/work-items creates the item in Pending; nothing here dispatches it further.
        mock.Setup(c => c.GetStatusAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Guid id, CancellationToken _) =>
                created.Any(w => w.Id == id) ? WorkItemStatus.Pending : null);

        mock.Setup(c => c.IsIssueDistributedAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string issueIdentifier, string providerConfigId, CancellationToken _) =>
                created.Any(w => w.Request.IssueIdentifier == issueIdentifier &&
                                 w.Request.IssueProviderConfigId == providerConfigId));

        mock.Setup(c => c.GetActiveIdentifiersAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => (IReadOnlyList<(string IssueIdentifier, string IssueProviderConfigId)>)created
                .Select(w => (w.Request.IssueIdentifier.Value, w.Request.IssueProviderConfigId))
                .Distinct()
                .ToList());

        return mock.Object;
    }
}
