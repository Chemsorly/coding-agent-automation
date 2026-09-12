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

    private static JobDistributionRequest MakeRequest(
        WorkItemTaskType taskType = WorkItemTaskType.Implementation) => new()
    {
        IssueIdentifier = new IssueIdentifier("GH-1"),
        IssueProviderConfigId = "github",
        RepoProviderConfigId = "github-repo",
        InitiatedBy = "test",
        TaskType = taskType,
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
        // Non-Consolidation task types must call CreateAsync (Pending path).
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
    public async Task DistributeAsync_When409Conflict_ReturnsSuccessQueued_IdempotentAlreadyQueued()
    {
        // 409 from CreateAsync means a live WorkItem already exists for this issue.
        // Treated as idempotent success (Queued=true) to avoid a label-revert loop.
        _client.Setup(c => c.CreateAsync(It.IsAny<JobDistributionRequest>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("conflict", null, System.Net.HttpStatusCode.Conflict));

        var result = await _sut.DistributeAsync(MakeRequest(), CancellationToken.None);

        result.Success.Should().BeTrue("409 is treated as already-queued, not a failure");
        result.Queued.Should().BeTrue();
        result.ErrorMessage.Should().BeNull();
    }

    [Fact]
    public async Task DistributeAsync_When503ServiceUnavailable_ReturnsFailureResult()
    {
        // 503 from CreateAsync indicates a transient API error (the server is unavailable).
        _client.Setup(c => c.CreateAsync(It.IsAny<JobDistributionRequest>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("server error", null, System.Net.HttpStatusCode.ServiceUnavailable));

        var result = await _sut.DistributeAsync(MakeRequest(), CancellationToken.None);

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("server error", "error message must propagate the underlying failure text");
    }

    [Fact]
    public async Task DistributeAsync_Consolidation_CallsDispatchAsync_ReturnsQueuedFalse()
    {
        // Consolidation routes through the synchronous DispatchAsync path (Dispatched directly).
        var workItemId = Guid.NewGuid();
        var request = MakeRequest(taskType: WorkItemTaskType.Consolidation);
        _client.Setup(c => c.DispatchAsync(It.IsAny<JobDistributionRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(workItemId);

        var result = await _sut.DistributeAsync(request, CancellationToken.None);

        result.Success.Should().BeTrue();
        result.Queued.Should().BeFalse("Consolidation uses synchronous dispatch, not Pending queue");
        _client.Verify(c => c.DispatchAsync(It.IsAny<JobDistributionRequest>(), It.IsAny<CancellationToken>()), Times.Once);
        _client.Verify(c => c.CreateAsync(It.IsAny<JobDistributionRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task DistributeAsync_Consolidation_When422_ReturnsFailureWithPermanentTrue()
    {
        // 422 Unprocessable from DispatchAsync: no job template for the agent selector.
        // This is a permanent configuration error — Permanent=true must be set so the caller
        // (ConsolidationDispatcher) can cascade the run to Failed instead of leaving it Queued.
        var request = MakeRequest(taskType: WorkItemTaskType.Consolidation);
        _client.Setup(c => c.DispatchAsync(It.IsAny<JobDistributionRequest>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("Unprocessable", null, System.Net.HttpStatusCode.UnprocessableEntity));

        var result = await _sut.DistributeAsync(request, CancellationToken.None);

        result.Success.Should().BeFalse("422 is a permanent failure — no template exists for the selector");
        result.Permanent.Should().BeTrue("422 means no job template — this cannot be resolved by retrying");
        result.Queued.Should().BeFalse();
        result.ErrorMessage.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task DistributeAsync_Consolidation_When409_ReturnsTransientFailureNotPermanent()
    {
        // 409 from DispatchAsync: concurrency limit reached — transient, retry on restart.
        var request = MakeRequest(taskType: WorkItemTaskType.Consolidation);
        _client.Setup(c => c.DispatchAsync(It.IsAny<JobDistributionRequest>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("Conflict", null, System.Net.HttpStatusCode.Conflict));

        var result = await _sut.DistributeAsync(request, CancellationToken.None);

        result.Success.Should().BeFalse();
        result.Permanent.Should().BeFalse("409 (concurrency) is transient — keep run Queued for retry");
    }

    [Fact]
    public async Task DistributeAsync_Consolidation_When503_ReturnsTransientFailureNotPermanent()
    {
        // 503 from DispatchAsync: PVC unavailable — transient, retry on restart.
        var request = MakeRequest(taskType: WorkItemTaskType.Consolidation);
        _client.Setup(c => c.DispatchAsync(It.IsAny<JobDistributionRequest>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("Service Unavailable", null, System.Net.HttpStatusCode.ServiceUnavailable));

        var result = await _sut.DistributeAsync(request, CancellationToken.None);

        result.Success.Should().BeFalse();
        result.Permanent.Should().BeFalse("503 (PVC unavailable) is transient — keep run Queued for retry");
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
