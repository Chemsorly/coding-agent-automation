using System.Net;
using AwesomeAssertions;
using CodingAgent.Api.Client;
using CodingAgent.Infrastructure.Persistence;
using CodingAgent.Infrastructure.Persistence.Services;
using CodingAgent.Orchestration.Dispatch;
using CodingAgent.Pipeline.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;

namespace CodingAgent.Web.UnitTests.Dispatch;

/// <summary>
/// Verifies that <see cref="KubernetesWorkDistributor.DistributeAsync"/> calls
/// <see cref="IPipelineApiWorkItemClient.CreateAsync"/> (the Pending enqueue endpoint)
/// and returns <c>Queued=true</c> (item is in the visible UI queue, not yet dispatched).
/// </summary>
public class KubernetesWorkDistributorApiTests
{
    private readonly Mock<IPipelineApiWorkItemClient> _mockClient;
    private readonly KubernetesWorkDistributor _sut;

    public KubernetesWorkDistributorApiTests()
    {
        _mockClient = new Mock<IPipelineApiWorkItemClient>();
        _sut = new KubernetesWorkDistributor(
            _mockClient.Object,
            Mock.Of<ILogger<KubernetesWorkDistributor>>());
    }

    // ── DistributeAsync routes by task type ─────────────────────────────

    [Fact]
    public async Task DistributeAsync_ImplementationRequest_CallsCreateAsync_NotDispatchAsync_InApiTests()
    {
        // Non-Consolidation requests must use CreateAsync (Pending enqueue path).
        var workItemId = Guid.NewGuid();
        var request = CreateMinimalRequest();

        _mockClient
            .Setup(c => c.CreateAsync(It.IsAny<JobDistributionRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(workItemId);

        await _sut.DistributeAsync(request, CancellationToken.None);

        // Verify CreateAsync was called (Pending enqueue path)
        _mockClient.Verify(
            c => c.CreateAsync(
                It.Is<JobDistributionRequest>(r =>
                    r.IssueIdentifier == request.IssueIdentifier &&
                    r.IssueProviderConfigId == request.IssueProviderConfigId),
                It.IsAny<CancellationToken>()),
            Times.Once);

        // Verify DispatchAsync was NOT called (synchronous dispatch path is bypassed)
        _mockClient.Verify(
            c => c.DispatchAsync(It.IsAny<JobDistributionRequest>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task DistributeAsync_ImplementationRequest_ReturnsSuccessResult_WithQueued_True()
    {
        var workItemId = Guid.NewGuid();
        var request = CreateMinimalRequest();

        _mockClient
            .Setup(c => c.CreateAsync(It.IsAny<JobDistributionRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(workItemId);

        var result = await _sut.DistributeAsync(request, CancellationToken.None);

        result.Success.Should().BeTrue();
        result.WorkItemId.Should().Be(workItemId.ToString());
        // Pending enqueue path: Queued=true — item is in the visible UI queue, not yet Dispatched
        result.Queued.Should().BeTrue("enqueue path returns Queued=true so label swap is deferred until pod is created");
    }

    [Fact]
    public async Task DistributeAsync_WhenClientThrows_ReturnsFailureResult()
    {
        var request = CreateMinimalRequest();

        _mockClient
            .Setup(c => c.CreateAsync(It.IsAny<JobDistributionRequest>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("Pipeline API unreachable"));

        var result = await _sut.DistributeAsync(request, CancellationToken.None);

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("Pipeline API unreachable");
    }

    [Fact]
    public async Task DistributeAsync_When409Conflict_ReturnsSuccessQueued_IdempotentAlreadyQueued()
    {
        // 409 from CreateAsync means a live WorkItem already exists for this issue.
        // This is treated as idempotent success (Queued=true) to avoid a label-revert
        // loop: returning failure would cause DistributeAndFinalizeAsync to revert the
        // label to agent:next, which re-queues the issue and causes another 409 next cycle.
        var request = CreateMinimalRequest();

        _mockClient
            .Setup(c => c.CreateAsync(It.IsAny<JobDistributionRequest>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("conflict", null, System.Net.HttpStatusCode.Conflict));

        var result = await _sut.DistributeAsync(request, CancellationToken.None);

        result.Success.Should().BeTrue("409 is treated as already-queued, not a failure");
        result.Queued.Should().BeTrue("existing live WorkItem means the item is effectively queued");
        result.ErrorMessage.Should().BeNull();
    }

    [Fact]
    public async Task DistributeAsync_When503ServiceUnavailable_ReturnsFailureResult()
    {
        var request = CreateMinimalRequest();

        _mockClient
            .Setup(c => c.CreateAsync(It.IsAny<JobDistributionRequest>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("server error", null, System.Net.HttpStatusCode.ServiceUnavailable));

        var result = await _sut.DistributeAsync(request, CancellationToken.None);

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("server error", "error message must propagate the underlying failure text");
    }

    [Fact]
    public async Task DistributeAsync_ConsolidationRequest_CallsDispatchAsync_NotCreateAsync()
    {
        // Consolidation must use the synchronous DispatchAsync path so it is not orphaned
        // in a Pending state with no poller to claim it.
        var workItemId = Guid.NewGuid();
        var request = CreateConsolidationRequest();

        _mockClient
            .Setup(c => c.DispatchAsync(It.IsAny<JobDistributionRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(workItemId);

        var result = await _sut.DistributeAsync(request, CancellationToken.None);

        result.Success.Should().BeTrue();
        result.Queued.Should().BeFalse("Consolidation is dispatched synchronously, not via Pending queue");
        result.WorkItemId.Should().Be(workItemId.ToString());

        _mockClient.Verify(c => c.DispatchAsync(It.IsAny<JobDistributionRequest>(), It.IsAny<CancellationToken>()), Times.Once);
        _mockClient.Verify(c => c.CreateAsync(It.IsAny<JobDistributionRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task DistributeAsync_ImplementationRequest_CallsCreateAsync_NotDispatchAsync()
    {
        // Implementation (and Review/Decomposition) must use the Pending enqueue path.
        var workItemId = Guid.NewGuid();
        var request = CreateMinimalRequest(); // TaskType = Implementation

        _mockClient
            .Setup(c => c.CreateAsync(It.IsAny<JobDistributionRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(workItemId);

        var result = await _sut.DistributeAsync(request, CancellationToken.None);

        result.Success.Should().BeTrue();
        result.Queued.Should().BeTrue();

        _mockClient.Verify(c => c.CreateAsync(It.IsAny<JobDistributionRequest>(), It.IsAny<CancellationToken>()), Times.Once);
        _mockClient.Verify(c => c.DispatchAsync(It.IsAny<JobDistributionRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task DistributeAsync_NullRequest_ThrowsArgumentNullException()
    {
        var act = () => _sut.DistributeAsync(null!, CancellationToken.None);
        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    private static JobDistributionRequest CreateMinimalRequest() => new()
    {
        IssueIdentifier = "org/repo#42",
        IssueProviderConfigId = "ip-1",
        RepoProviderConfigId = "rp-1",
        InitiatedBy = "api-test",
        TaskType = WorkItemTaskType.Implementation,
        AgentSelector = "default",
        TimeoutSeconds = 3600
    };

    private static JobDistributionRequest CreateConsolidationRequest() => new()
    {
        IssueIdentifier = "consolidation-run-42",
        IssueProviderConfigId = "consolidation",
        RepoProviderConfigId = "",
        InitiatedBy = "consolidation",
        TaskType = WorkItemTaskType.Consolidation,
        AgentSelector = "dotnet,kiro",
        TimeoutSeconds = 3600
    };
}
