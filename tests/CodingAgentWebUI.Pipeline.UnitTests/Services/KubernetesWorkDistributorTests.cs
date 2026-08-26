using AwesomeAssertions;
using CodingAgentWebUI.Api.Client;
using CodingAgentWebUI.Orchestration.Dispatch;
using CodingAgentWebUI.Pipeline.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace CodingAgentWebUI.Pipeline.UnitTests.Services;

/// <summary>
/// Tests for KubernetesWorkDistributor.
/// Covers: DistributeAsync (success/failure), CancelJobAsync (success/BadRequest/exception/invalid GUID),
/// GetJobStatusAsync (all WorkItemStatus values, null, invalid GUID),
/// IsIssueDistributedAsync, GetActiveIssueIdentifiersAsync.
/// </summary>
public sealed class KubernetesWorkDistributorTests
{
    private readonly Mock<IPipelineApiWorkItemClient> _client = new();
    private readonly KubernetesWorkDistributor _sut;

    public KubernetesWorkDistributorTests()
    {
        _sut = new KubernetesWorkDistributor(_client.Object, NullLogger<KubernetesWorkDistributor>.Instance);
    }

    private static JobDistributionRequest MakeRequest() => new()
    {
        IssueIdentifier = new IssueIdentifier("GH-1"),
        IssueProviderConfigId = "github",
        RepoProviderConfigId = "github-repo",
        InitiatedBy = "test",
        TaskType = WorkItemTaskType.Implementation,
        AgentSelector = "kiro",
        TimeoutSeconds = 3600
    };

    // ── Constructor guards ────────────────────────────────────────────────

    [Fact]
    public void Constructor_NullClient_Throws()
    {
        var act = () => new KubernetesWorkDistributor(null!, NullLogger<KubernetesWorkDistributor>.Instance);
        act.Should().Throw<ArgumentNullException>();
    }

    // ── DistributeAsync ───────────────────────────────────────────────────

    [Fact]
    public async Task DistributeAsync_OnSuccess_ReturnsSuccessResult()
    {
        var workItemId = Guid.NewGuid();
        _client.Setup(c => c.CreateAsync(It.IsAny<JobDistributionRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(workItemId);

        var result = await _sut.DistributeAsync(MakeRequest(), CancellationToken.None);

        result.Success.Should().BeTrue();
        result.WorkItemId.Should().Be(workItemId.ToString());
        result.Queued.Should().BeTrue();
        result.ErrorMessage.Should().BeNull();
    }

    [Fact]
    public async Task DistributeAsync_WhenClientThrows_ReturnsFailureResult()
    {
        _client.Setup(c => c.CreateAsync(It.IsAny<JobDistributionRequest>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("API unavailable"));

        var result = await _sut.DistributeAsync(MakeRequest(), CancellationToken.None);

        result.Success.Should().BeFalse();
        result.WorkItemId.Should().BeNull();
        result.ErrorMessage.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task DistributeAsync_NullRequest_Throws()
    {
        var act = () => _sut.DistributeAsync(null!, CancellationToken.None);
        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    // ── CancelJobAsync ────────────────────────────────────────────────────

    [Fact]
    public async Task CancelJobAsync_OnSuccess_ReturnsTrue()
    {
        var id = Guid.NewGuid();
        _client.Setup(c => c.PostStatusAsync(id, It.IsAny<WorkItemStatusUpdate>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var result = await _sut.CancelJobAsync(new JobId(id.ToString()), CancellationToken.None);

        result.Should().BeTrue();
    }

    [Fact]
    public async Task CancelJobAsync_On400_ReturnsFalse()
    {
        var id = Guid.NewGuid();
        _client.Setup(c => c.PostStatusAsync(id, It.IsAny<WorkItemStatusUpdate>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("Bad Request", null, System.Net.HttpStatusCode.BadRequest));

        var result = await _sut.CancelJobAsync(new JobId(id.ToString()), CancellationToken.None);

        result.Should().BeFalse();
    }

    [Fact]
    public async Task CancelJobAsync_OnOtherException_ReturnsFalse()
    {
        var id = Guid.NewGuid();
        _client.Setup(c => c.PostStatusAsync(id, It.IsAny<WorkItemStatusUpdate>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("Server Error", null, System.Net.HttpStatusCode.InternalServerError));

        var result = await _sut.CancelJobAsync(new JobId(id.ToString()), CancellationToken.None);

        result.Should().BeFalse();
    }

    [Fact]
    public async Task CancelJobAsync_InvalidGuid_ReturnsFalse()
    {
        var result = await _sut.CancelJobAsync(new JobId("not-a-guid"), CancellationToken.None);
        result.Should().BeFalse();
    }

    // ── GetJobStatusAsync ─────────────────────────────────────────────────

    [Theory]
    [InlineData(WorkItemStatus.Pending, JobDistributionStatus.Pending)]
    [InlineData(WorkItemStatus.Dispatched, JobDistributionStatus.Dispatched)]
    [InlineData(WorkItemStatus.Running, JobDistributionStatus.Running)]
    [InlineData(WorkItemStatus.Succeeded, JobDistributionStatus.Succeeded)]
    [InlineData(WorkItemStatus.Failed, JobDistributionStatus.Failed)]
    [InlineData(WorkItemStatus.Cancelled, JobDistributionStatus.Cancelled)]
    public async Task GetJobStatusAsync_MapsAllStatuses(WorkItemStatus workItemStatus, JobDistributionStatus expected)
    {
        var id = Guid.NewGuid();
        _client.Setup(c => c.GetStatusAsync(id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(workItemStatus);

        var result = await _sut.GetJobStatusAsync(new JobId(id.ToString()), CancellationToken.None);

        result.Should().Be(expected);
    }

    [Fact]
    public async Task GetJobStatusAsync_WhenNull_ReturnsUnknown()
    {
        var id = Guid.NewGuid();
        _client.Setup(c => c.GetStatusAsync(id, It.IsAny<CancellationToken>()))
            .ReturnsAsync((WorkItemStatus?)null);

        var result = await _sut.GetJobStatusAsync(new JobId(id.ToString()), CancellationToken.None);

        result.Should().Be(JobDistributionStatus.Unknown);
    }

    [Fact]
    public async Task GetJobStatusAsync_InvalidGuid_ReturnsUnknown()
    {
        var result = await _sut.GetJobStatusAsync(new JobId("bad-guid"), CancellationToken.None);
        result.Should().Be(JobDistributionStatus.Unknown);
    }

    // ── IsIssueDistributedAsync ───────────────────────────────────────────

    [Fact]
    public async Task IsIssueDistributedAsync_ReturnsClientResult()
    {
        _client.Setup(c => c.IsIssueDistributedAsync("GH-1", "github", It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var result = await _sut.IsIssueDistributedAsync(
            new IssueIdentifier("GH-1"), new ProviderConfigId("github"), CancellationToken.None);

        result.Should().BeTrue();
    }

    [Fact]
    public async Task IsIssueDistributedAsync_WhenFalse_ReturnsFalse()
    {
        _client.Setup(c => c.IsIssueDistributedAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        var result = await _sut.IsIssueDistributedAsync(
            new IssueIdentifier("GH-99"), new ProviderConfigId("github"), CancellationToken.None);

        result.Should().BeFalse();
    }

    // ── GetActiveIssueIdentifiersAsync ────────────────────────────────────

    [Fact]
    public async Task GetActiveIssueIdentifiersAsync_MapsToHashSet()
    {
        _client.Setup(c => c.GetActiveIdentifiersAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<(string IssueIdentifier, string IssueProviderConfigId)>
            {
                ("GH-1", "github"),
                ("GH-2", "github")
            } as IReadOnlyList<(string IssueIdentifier, string IssueProviderConfigId)>);

        var result = await _sut.GetActiveIssueIdentifiersAsync(CancellationToken.None);

        result.Should().HaveCount(2);
        result.Should().Contain((new IssueIdentifier("GH-1"), new ProviderConfigId("github")));
    }

    [Fact]
    public async Task GetActiveIssueIdentifiersAsync_WhenEmpty_ReturnsEmptyHashSet()
    {
        _client.Setup(c => c.GetActiveIdentifiersAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<(string, string)>() as IReadOnlyList<(string, string)>);

        var result = await _sut.GetActiveIssueIdentifiersAsync(CancellationToken.None);

        result.Should().BeEmpty();
    }
}
