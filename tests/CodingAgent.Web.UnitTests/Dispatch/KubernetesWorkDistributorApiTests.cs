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
        // Flag off (default): Consolidation must use the legacy synchronous DispatchAsync path
        // so the WorkItem is not orphaned in a Pending state with no poller to claim it.
        // _sut is constructed without the flag (unifiedDispatchEnabled=false by default).
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

    // ── Consolidation (synchronous dispatch) error paths ─────────────────

    [Fact]
    public async Task DistributeAsync_ConsolidationRequest_When409Conflict_ReturnsFailureNoCapacity()
    {
        // The synchronous dispatch endpoint returns 409 when the concurrency cap is reached.
        // This is surfaced as a failure result (no capacity), not a thrown exception.
        var request = CreateConsolidationRequest();

        _mockClient
            .Setup(c => c.DispatchAsync(It.IsAny<JobDistributionRequest>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("conflict", null, HttpStatusCode.Conflict));

        var result = await _sut.DistributeAsync(request, CancellationToken.None);

        result.Success.Should().BeFalse();
        result.Queued.Should().BeFalse();
        result.ErrorMessage.Should().Contain("No capacity", "409 on the consolidation dispatch path means no capacity");
    }

    [Fact]
    public async Task DistributeAsync_ConsolidationRequest_When503ServiceUnavailable_ReturnsFailureNoCapacity()
    {
        // 503 (no PVC available / K8s failure) on the synchronous dispatch path is also treated
        // as a no-capacity failure so the Scheduler can retry the issue next cycle.
        var request = CreateConsolidationRequest();

        _mockClient
            .Setup(c => c.DispatchAsync(It.IsAny<JobDistributionRequest>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("server error", null, HttpStatusCode.ServiceUnavailable));

        var result = await _sut.DistributeAsync(request, CancellationToken.None);

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("No capacity");
    }

    [Fact]
    public async Task DistributeAsync_ConsolidationRequest_WhenDispatchThrowsUnexpected_ReturnsFailure()
    {
        // A non-HTTP error from the dispatch endpoint is caught by the general handler and
        // returned as a failure result rather than propagating out of DistributeAsync.
        var request = CreateConsolidationRequest();

        _mockClient
            .Setup(c => c.DispatchAsync(It.IsAny<JobDistributionRequest>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("unexpected dispatch error"));

        var result = await _sut.DistributeAsync(request, CancellationToken.None);

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("unexpected dispatch error");
    }

    // ── Consolidation (unified dispatch path, flag=on) ────────────────────

    [Fact]
    public async Task DistributeAsync_ConsolidationRequest_FlagOn_CallsCreateAsync_NotDispatchAsync()
    {
        // Flag on: Consolidation must use the Pending enqueue path (CreateAsync), same as all
        // other task types. The poller applies RunType tier ordering (#2563) before pod creation.
        var workItemId = Guid.NewGuid();
        var request = CreateConsolidationRequest();
        var sut = CreateFlagOnSut();

        _mockClient
            .Setup(c => c.CreateAsync(It.IsAny<JobDistributionRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(workItemId);

        var result = await sut.DistributeAsync(request, CancellationToken.None);

        result.Success.Should().BeTrue();
        result.WorkItemId.Should().Be(workItemId.ToString());
        result.Queued.Should().BeTrue("unified dispatch path enqueues as Pending, not Dispatched");

        _mockClient.Verify(c => c.CreateAsync(It.IsAny<JobDistributionRequest>(), It.IsAny<CancellationToken>()), Times.Once);
        _mockClient.Verify(c => c.DispatchAsync(It.IsAny<JobDistributionRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task DistributeAsync_ConsolidationRequest_FlagOn_When409_ReturnsSuccess_Idempotent()
    {
        // Flag on: 409 on the Pending enqueue path means a live WorkItem already exists for this
        // consolidation run — treated as idempotent success (Queued=true), same as all other task
        // types. This prevents a requeue loop if rehydration calls dispatch twice for the same run.
        var request = CreateConsolidationRequest();
        var sut = CreateFlagOnSut();

        _mockClient
            .Setup(c => c.CreateAsync(It.IsAny<JobDistributionRequest>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("conflict", null, HttpStatusCode.Conflict));

        var result = await sut.DistributeAsync(request, CancellationToken.None);

        result.Success.Should().BeTrue("409 on unified dispatch path is treated as already-queued, not a failure");
        result.Queued.Should().BeTrue("existing live WorkItem means the item is effectively queued");
        result.ErrorMessage.Should().BeNull();
        // TODO: WorkItemId is not asserted here. On the 409 idempotent path, WorkItemId may be
        // null (no new item was created). Callers that log or store the WorkItemId from the result
        // would silently receive null. Add an assertion on result.WorkItemId (expected null on 409)
        // once the expected contract for the idempotent case is confirmed. (#2564 review finding)
    }

    [Fact]
    public async Task DistributeAsync_ConsolidationRequest_FlagOn_FlagOff_RoutesToDifferentPaths()
    {
        // Verify the flag routes correctly in both states for the same Consolidation request.
        // Flag off → DispatchAsync (synchronous). Flag on → CreateAsync (Pending enqueue).
        var workItemId = Guid.NewGuid();
        var request = CreateConsolidationRequest();

        _mockClient
            .Setup(c => c.DispatchAsync(It.IsAny<JobDistributionRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(workItemId);
        _mockClient
            .Setup(c => c.CreateAsync(It.IsAny<JobDistributionRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(workItemId);

        // Flag off (default _sut) → synchronous path
        var flagOffResult = await _sut.DistributeAsync(request, CancellationToken.None);
        flagOffResult.Queued.Should().BeFalse("flag=off dispatches synchronously, not via Pending queue");

        // Flag on → Pending enqueue path
        var flagOnResult = await CreateFlagOnSut().DistributeAsync(request, CancellationToken.None);
        flagOnResult.Queued.Should().BeTrue("flag=on enqueues as Pending");
        // TODO: This combination test only asserts on Queued, not on which mock methods were
        // called. If the routing condition were accidentally inverted, this test would still pass
        // because both mocks are set up and Queued is set by whichever path runs. The per-flag
        // dedicated tests (FlagOn_CallsCreateAsync_NotDispatchAsync and the existing flag-off test)
        // carry the method-call verification. If this combination test is ever strengthened, add
        // Verify(DispatchAsync, Times.Once) after the flag-off call and Verify(CreateAsync,
        // Times.Once) after the flag-on call, using a fresh mock per invocation to avoid
        // accumulated call history. (#2564 review finding)
    }

    [Fact]
    public async Task DistributeAsync_ImplementationRequest_WhenCreateThrowsUnexpected_ReturnsFailure()
    {
        // A non-HttpRequestException from CreateAsync (e.g. a serialization or programming error)
        // is caught by the general handler on the enqueue path and returned as a failure result.
        var request = CreateMinimalRequest();

        _mockClient
            .Setup(c => c.CreateAsync(It.IsAny<JobDistributionRequest>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("unexpected enqueue error"));

        var result = await _sut.DistributeAsync(request, CancellationToken.None);

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("unexpected enqueue error");
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

    /// <summary>
    /// Creates a <see cref="KubernetesWorkDistributor"/> with the unified dispatch flag enabled.
    /// Uses the shared <see cref="_mockClient"/> so that Moq <c>Verify</c> calls work correctly.
    /// </summary>
    private KubernetesWorkDistributor CreateFlagOnSut() =>
        new(_mockClient.Object, Mock.Of<ILogger<KubernetesWorkDistributor>>(), unifiedDispatchEnabled: true);
}
