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

    // ── DistributeAsync calls CreateAsync (not DispatchAsync) ─────────────

    [Fact]
    public async Task DistributeAsync_CallsCreateAsync_NotDispatchAsync()
    {
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
    public async Task DistributeAsync_ReturnsSuccessResult_WithQueued_True()
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
    public async Task DistributeAsync_When409Conflict_ReturnsFailureResult()
    {
        var request = CreateMinimalRequest();

        _mockClient
            .Setup(c => c.CreateAsync(It.IsAny<JobDistributionRequest>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("conflict", null, System.Net.HttpStatusCode.Conflict));

        var result = await _sut.DistributeAsync(request, CancellationToken.None);

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().NotBeNullOrEmpty();
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
        result.ErrorMessage.Should().NotBeNullOrEmpty();
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
}
