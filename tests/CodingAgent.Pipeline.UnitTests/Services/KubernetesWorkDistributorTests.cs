using AwesomeAssertions;
using CodingAgent.Api.Client;
using CodingAgent.Orchestration.Dispatch;
using CodingAgent.Pipeline.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace CodingAgent.Pipeline.UnitTests.Services;

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
        // Pending enqueue path: Queued=true (item is Pending in the visible UI queue, not yet Dispatched)
        result.Queued.Should().BeTrue("enqueue path returns Queued=true so DistributeAndFinalizeAsync defers the label swap");
        result.ErrorMessage.Should().BeNull();
    }

    [Fact]
    public async Task DistributeAsync_OnSuccess_CallsCreateAsync_NotDispatchAsync()
    {
        // DistributeAsync must call CreateAsync (Pending path) so the item is visible in the UI queue.
        // Calling DispatchAsync (Dispatched path) would skip the Pending state entirely.
        var workItemId = Guid.NewGuid();
        _client.Setup(c => c.CreateAsync(It.IsAny<JobDistributionRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(workItemId);

        await _sut.DistributeAsync(MakeRequest(), CancellationToken.None);

        _client.Verify(c => c.CreateAsync(It.IsAny<JobDistributionRequest>(), It.IsAny<CancellationToken>()), Times.Once,
            "DistributeAsync must call CreateAsync to create a Pending WorkItem visible in the UI queue");
        _client.Verify(c => c.DispatchAsync(It.IsAny<JobDistributionRequest>(), It.IsAny<CancellationToken>()), Times.Never,
            "DistributeAsync must NOT call DispatchAsync — that skips the Pending queue entirely");
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
    public async Task DistributeAsync_When409Conflict_ReturnsFailureResult()
    {
        // 409 from CreateAsync means a live WorkItem already exists for this issue
        // (the partial unique index on (IssueIdentifier, IssueProviderConfigId) rejects it).
        // This is different from the old synchronous-dispatch path where 409 meant
        // "concurrency limit reached". Capacity enforcement now lives in WorkItemDispatchService,
        // which respects maxConcurrent when picking up Pending items; CreateAsync itself does
        // not enforce concurrency.
        _client.Setup(c => c.CreateAsync(It.IsAny<JobDistributionRequest>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("conflict", null, System.Net.HttpStatusCode.Conflict));

        var result = await _sut.DistributeAsync(MakeRequest(), CancellationToken.None);

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task DistributeAsync_When503ServiceUnavailable_ReturnsFailureResult()
    {
        // 503 from CreateAsync indicates a transient API error (the server is unavailable).
        // The old synchronous-dispatch path used 503 to signal "no PVC available"; that
        // capacity check is now inside WorkItemDispatchService. CreateAsync returns 503
        // only for true infrastructure failures.
        _client.Setup(c => c.CreateAsync(It.IsAny<JobDistributionRequest>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("server error", null, System.Net.HttpStatusCode.ServiceUnavailable));

        var result = await _sut.DistributeAsync(MakeRequest(), CancellationToken.None);

        result.Success.Should().BeFalse();
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
