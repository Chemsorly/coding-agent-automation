using AwesomeAssertions;
using CodingAgentWebUI.Api.Client;
using CodingAgentWebUI.Orchestration.Dispatch;
using CodingAgentWebUI.Pipeline.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace CodingAgentWebUI.Infrastructure.UnitTests.Persistence;

/// <summary>
/// Unit tests for <see cref="KubernetesWorkDistributor"/>.
/// All operations are now API-backed; tests use <see cref="Mock{IPipelineApiWorkItemClient}"/>.
/// </summary>
public class KubernetesWorkDistributorTests
{
    private readonly Mock<IPipelineApiWorkItemClient> _mockApiClient = new();
    private readonly KubernetesWorkDistributor _distributor;

    public KubernetesWorkDistributorTests()
    {
        _distributor = new KubernetesWorkDistributor(
            _mockApiClient.Object,
            NullLogger<KubernetesWorkDistributor>.Instance);
    }

    // ── DistributeAsync ──────────────────────────────────────────────────

    [Fact]
    public async Task DistributeAsync_CallsApiClientCreateAsync()
    {
        var request = CreateRequest("owner/repo#1", "provider-1");
        _mockApiClient
            .Setup(c => c.CreateAsync(It.IsAny<JobDistributionRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Guid.NewGuid());

        await _distributor.DistributeAsync(request, CancellationToken.None);

        _mockApiClient.Verify(
            c => c.CreateAsync(
                It.Is<JobDistributionRequest>(r => r.IssueIdentifier == request.IssueIdentifier),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task DistributeAsync_ReturnsSuccessWithWorkItemId()
    {
        var expectedId = Guid.NewGuid();
        _mockApiClient
            .Setup(c => c.CreateAsync(It.IsAny<JobDistributionRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(expectedId);

        var request = CreateRequest("owner/repo#2", "provider-2");
        var result = await _distributor.DistributeAsync(request, CancellationToken.None);

        result.Success.Should().BeTrue();
        result.Queued.Should().BeTrue();
        result.WorkItemId.Should().Be(expectedId.ToString());
        result.ErrorMessage.Should().BeNull();
    }

    [Fact]
    public async Task DistributeAsync_WhenApiThrows_ReturnsFailureResult()
    {
        _mockApiClient
            .Setup(c => c.CreateAsync(It.IsAny<JobDistributionRequest>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("Pipeline API unreachable"));

        var request = CreateRequest("owner/repo#3", "provider-3");
        var result = await _distributor.DistributeAsync(request, CancellationToken.None);

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("Pipeline API unreachable");
    }

    // ── CancelJobAsync ───────────────────────────────────────────────────

    [Fact]
    public async Task CancelJobAsync_ValidGuid_CallsPostStatusCancelled()
    {
        var workItemId = Guid.NewGuid();
        _mockApiClient
            .Setup(c => c.PostStatusAsync(workItemId, It.IsAny<WorkItemStatusUpdate>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var result = await _distributor.CancelJobAsync(workItemId.ToString(), CancellationToken.None);

        result.Should().BeTrue();
        _mockApiClient.Verify(c => c.PostStatusAsync(
            workItemId,
            It.Is<WorkItemStatusUpdate>(u => u.Status == "Cancelled"),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task CancelJobAsync_InvalidGuid_ReturnsFalse()
    {
        var result = await _distributor.CancelJobAsync("not-a-guid", CancellationToken.None);
        result.Should().BeFalse();
        _mockApiClient.Verify(c => c.PostStatusAsync(
            It.IsAny<Guid>(), It.IsAny<WorkItemStatusUpdate>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task CancelJobAsync_ApiBadRequest_ReturnsFalse()
    {
        var workItemId = Guid.NewGuid();
        _mockApiClient
            .Setup(c => c.PostStatusAsync(workItemId, It.IsAny<WorkItemStatusUpdate>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("Bad request", null, System.Net.HttpStatusCode.BadRequest));

        var result = await _distributor.CancelJobAsync(workItemId.ToString(), CancellationToken.None);

        result.Should().BeFalse();
    }

    // ── GetJobStatusAsync ────────────────────────────────────────────────

    [Fact]
    public async Task GetJobStatusAsync_ApiReturnsPending_ReturnsPending()
    {
        var workItemId = Guid.NewGuid();
        _mockApiClient
            .Setup(c => c.GetStatusAsync(workItemId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(WorkItemStatus.Pending);

        var status = await _distributor.GetJobStatusAsync(workItemId.ToString(), CancellationToken.None);

        status.Should().Be(JobDistributionStatus.Pending);
    }

    [Fact]
    public async Task GetJobStatusAsync_ApiReturnsNull_ReturnsUnknown()
    {
        var workItemId = Guid.NewGuid();
        _mockApiClient
            .Setup(c => c.GetStatusAsync(workItemId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((WorkItemStatus?)null);

        var status = await _distributor.GetJobStatusAsync(workItemId.ToString(), CancellationToken.None);

        status.Should().Be(JobDistributionStatus.Unknown);
    }

    [Fact]
    public async Task GetJobStatusAsync_InvalidGuid_ReturnsUnknown()
    {
        var status = await _distributor.GetJobStatusAsync("invalid", CancellationToken.None);
        status.Should().Be(JobDistributionStatus.Unknown);
        _mockApiClient.Verify(c => c.GetStatusAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // ── IsIssueDistributedAsync ──────────────────────────────────────────

    [Fact]
    public async Task IsIssueDistributedAsync_ApiReturnsTrue_ReturnsTrue()
    {
        _mockApiClient
            .Setup(c => c.IsIssueDistributedAsync("owner/repo#7", "provider-7", It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var result = await _distributor.IsIssueDistributedAsync(
            "owner/repo#7", "provider-7", CancellationToken.None);

        result.Should().BeTrue();
    }

    [Fact]
    public async Task IsIssueDistributedAsync_ApiReturnsFalse_ReturnsFalse()
    {
        _mockApiClient
            .Setup(c => c.IsIssueDistributedAsync("nonexistent", "provider-x", It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        var result = await _distributor.IsIssueDistributedAsync(
            "nonexistent", "provider-x", CancellationToken.None);

        result.Should().BeFalse();
    }

    // ── GetActiveIssueIdentifiersAsync ────────────────────────────────────

    [Fact]
    public async Task GetActiveIssueIdentifiersAsync_ReturnsApiPairs()
    {
        _mockApiClient
            .Setup(c => c.GetActiveIdentifiersAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([("active-1", "p1"), ("active-2", "p2")]);

        var result = await _distributor.GetActiveIssueIdentifiersAsync(CancellationToken.None);

        result.Should().HaveCount(2);
        result.Should().Contain(("active-1", "p1"));
        result.Should().Contain(("active-2", "p2"));
    }

    [Fact]
    public async Task GetActiveIssueIdentifiersAsync_EmptyApi_ReturnsEmptySet()
    {
        _mockApiClient
            .Setup(c => c.GetActiveIdentifiersAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);

        var result = await _distributor.GetActiveIssueIdentifiersAsync(CancellationToken.None);

        result.Should().BeEmpty();
    }

    // ── Helpers ───────────────────────────────────────────────────────────

    private static JobDistributionRequest CreateRequest(string issueId, string providerId) => new()
    {
        IssueIdentifier = issueId,
        IssueProviderConfigId = providerId,
        RepoProviderConfigId = "repo-provider-1",
        InitiatedBy = "pipeline-loop",
        TaskType = WorkItemTaskType.Implementation,
        AgentSelector = "kiro,linux",
        TimeoutSeconds = 1800,
        ProjectId = "proj-1",
        RunType = PipelineRunType.Implementation
    };
}


// ── Additional coverage: MapStatus exhaustive, CancelJobAsync non-BadRequest ──────

/// <summary>
/// Additional tests extending <see cref="KubernetesWorkDistributorTests"/> to cover
/// the remaining branches: the full <c>MapStatus</c> enum mapping and the
/// non-BadRequest exception path in <c>CancelJobAsync</c>.
/// </summary>
public class KubernetesWorkDistributorAdditionalTests
{
    private readonly Mock<IPipelineApiWorkItemClient> _mockApiClient = new();
    private readonly KubernetesWorkDistributor _distributor;

    public KubernetesWorkDistributorAdditionalTests()
    {
        _distributor = new KubernetesWorkDistributor(
            _mockApiClient.Object,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<KubernetesWorkDistributor>.Instance);
    }

    // ── MapStatus — exhaustive coverage via GetJobStatusAsync ────────────

    [Theory]
    [InlineData(WorkItemStatus.Pending, JobDistributionStatus.Pending)]
    [InlineData(WorkItemStatus.Dispatched, JobDistributionStatus.Dispatched)]
    [InlineData(WorkItemStatus.Running, JobDistributionStatus.Running)]
    [InlineData(WorkItemStatus.Succeeded, JobDistributionStatus.Succeeded)]
    [InlineData(WorkItemStatus.Failed, JobDistributionStatus.Failed)]
    [InlineData(WorkItemStatus.Cancelled, JobDistributionStatus.Cancelled)]
    public async Task GetJobStatusAsync_MapsAllWorkItemStatuses(
        WorkItemStatus apiStatus, JobDistributionStatus expectedDistributionStatus)
    {
        var workItemId = Guid.NewGuid();
        _mockApiClient
            .Setup(c => c.GetStatusAsync(workItemId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(apiStatus);

        var result = await _distributor.GetJobStatusAsync(workItemId.ToString(), CancellationToken.None);

        result.Should().Be(expectedDistributionStatus);
    }

    [Fact]
    public async Task GetJobStatusAsync_UnknownEnumValue_ReturnsUnknown()
    {
        // Simulate an API returning an enum value not in our switch (future-proofing)
        var workItemId = Guid.NewGuid();
        _mockApiClient
            .Setup(c => c.GetStatusAsync(workItemId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((WorkItemStatus)999);

        var result = await _distributor.GetJobStatusAsync(workItemId.ToString(), CancellationToken.None);

        result.Should().Be(JobDistributionStatus.Unknown);
    }

    // ── CancelJobAsync — non-BadRequest HttpRequestException returns false ─

    [Fact]
    public async Task CancelJobAsync_ApiNonBadRequestHttpException_ReturnsFalse()
    {
        // A 500 or network error should also return false, not propagate the exception.
        var workItemId = Guid.NewGuid();
        _mockApiClient
            .Setup(c => c.PostStatusAsync(workItemId, It.IsAny<WorkItemStatusUpdate>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("Server error", null, System.Net.HttpStatusCode.InternalServerError));

        var result = await _distributor.CancelJobAsync(workItemId.ToString(), CancellationToken.None);

        result.Should().BeFalse("non-BadRequest HTTP errors must also be swallowed and return false");
    }

    [Fact]
    public async Task CancelJobAsync_GenericException_ReturnsFalse()
    {
        var workItemId = Guid.NewGuid();
        _mockApiClient
            .Setup(c => c.PostStatusAsync(workItemId, It.IsAny<WorkItemStatusUpdate>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new TimeoutException("Request timed out"));

        var result = await _distributor.CancelJobAsync(workItemId.ToString(), CancellationToken.None);

        result.Should().BeFalse("generic exceptions must also be swallowed and return false");
    }
}
