using AwesomeAssertions;
using CodingAgentWebUI.Api.Client;
using CodingAgentWebUI.Orchestration.Dispatch;
using CodingAgentWebUI.Pipeline.Models;
using Microsoft.Extensions.Logging;
using Moq;

namespace CodingAgentWebUI.UnitTests.Dispatch;

/// <summary>
/// Verifies that <see cref="KubernetesWorkDistributor.DistributeAsync"/> calls
/// <see cref="IPipelineApiWorkItemClient.DispatchAsync"/> (synchronous dispatch path, issue #2322).
/// The two-hop Pending→DispatchLoop→K8s path has been replaced with a single synchronous call.
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

    // ── DistributeAsync calls DispatchAsync (synchronous dispatch path) ───

    [Fact]
    public async Task DistributeAsync_CallsApiClientDispatchAsync_WithSameRequest()
    {
        var workItemId = Guid.NewGuid();
        var request = CreateMinimalRequest();

        _mockClient
            .Setup(c => c.DispatchAsync(It.IsAny<JobDistributionRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DispatchWorkItemResponse(workItemId));

        await _sut.DistributeAsync(request, CancellationToken.None);

        _mockClient.Verify(
            c => c.DispatchAsync(
                It.Is<JobDistributionRequest>(r =>
                    r.IssueIdentifier == request.IssueIdentifier &&
                    r.IssueProviderConfigId == request.IssueProviderConfigId),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task DistributeAsync_ReturnsSuccessResult_WithWorkItemIdFromApi()
    {
        var workItemId = Guid.NewGuid();
        var request = CreateMinimalRequest();

        _mockClient
            .Setup(c => c.DispatchAsync(It.IsAny<JobDistributionRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DispatchWorkItemResponse(workItemId));

        var result = await _sut.DistributeAsync(request, CancellationToken.None);

        result.Success.Should().BeTrue();
        result.WorkItemId.Should().Be(workItemId.ToString());
        // Queued: false — item is immediately Dispatched, no longer queued as Pending
        result.Queued.Should().BeFalse();
    }

    [Fact]
    public async Task DistributeAsync_WhenApiThrows_ReturnFailureResult()
    {
        var request = CreateMinimalRequest();

        _mockClient
            .Setup(c => c.DispatchAsync(It.IsAny<JobDistributionRequest>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("Pipeline API unreachable"));

        var result = await _sut.DistributeAsync(request, CancellationToken.None);

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("Pipeline API unreachable");
    }

    [Fact]
    public async Task DistributeAsync_WhenApi503_ReturnFailureResult()
    {
        var request = CreateMinimalRequest();

        _mockClient
            .Setup(c => c.DispatchAsync(It.IsAny<JobDistributionRequest>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("Service Unavailable", null, System.Net.HttpStatusCode.ServiceUnavailable));

        var result = await _sut.DistributeAsync(request, CancellationToken.None);

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task DistributeAsync_WhenApi409_ReturnFailureResult()
    {
        var request = CreateMinimalRequest();

        _mockClient
            .Setup(c => c.DispatchAsync(It.IsAny<JobDistributionRequest>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("Conflict", null, System.Net.HttpStatusCode.Conflict));

        var result = await _sut.DistributeAsync(request, CancellationToken.None);

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task DistributeAsync_DoesNotCallCreateAsync()
    {
        var workItemId = Guid.NewGuid();
        var request = CreateMinimalRequest();

        _mockClient
            .Setup(c => c.DispatchAsync(It.IsAny<JobDistributionRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DispatchWorkItemResponse(workItemId));

        await _sut.DistributeAsync(request, CancellationToken.None);

        // Verify the old CreateAsync (Pending path) is NOT called
        _mockClient.Verify(c => c.CreateAsync(It.IsAny<JobDistributionRequest>(), It.IsAny<CancellationToken>()), Times.Never);
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
