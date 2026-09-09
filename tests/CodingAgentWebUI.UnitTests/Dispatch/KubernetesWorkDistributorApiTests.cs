using System.Net;
using AwesomeAssertions;
using CodingAgentWebUI.Api.Client;
using CodingAgentWebUI.Infrastructure.Persistence;
using CodingAgentWebUI.Infrastructure.Persistence.Services;
using CodingAgentWebUI.Orchestration.Dispatch;
using CodingAgentWebUI.Pipeline.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;

namespace CodingAgentWebUI.UnitTests.Dispatch;

/// <summary>
/// Verifies that <see cref="KubernetesWorkDistributor.DistributeAsync"/> calls
/// <see cref="IPipelineApiWorkItemClient.DispatchAsync"/> (the synchronous dispatch endpoint)
/// and returns <c>Queued=false</c> (item is immediately Dispatched, not in the Pending queue).
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

    // ── DistributeAsync calls DispatchAsync (not CreateAsync) ─────────────

    [Fact]
    public async Task DistributeAsync_CallsDispatchAsync_NotCreateAsync()
    {
        var workItemId = Guid.NewGuid();
        var request = CreateMinimalRequest();

        _mockClient
            .Setup(c => c.DispatchAsync(It.IsAny<JobDistributionRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(workItemId);

        await _sut.DistributeAsync(request, CancellationToken.None);

        // Verify DispatchAsync was called (not CreateAsync)
        _mockClient.Verify(
            c => c.DispatchAsync(
                It.Is<JobDistributionRequest>(r =>
                    r.IssueIdentifier == request.IssueIdentifier &&
                    r.IssueProviderConfigId == request.IssueProviderConfigId),
                It.IsAny<CancellationToken>()),
            Times.Once);

        // Verify CreateAsync was NOT called (old Pending-queue path)
        _mockClient.Verify(
            c => c.CreateAsync(It.IsAny<JobDistributionRequest>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task DistributeAsync_ReturnsSuccessResult_WithQueued_False()
    {
        var workItemId = Guid.NewGuid();
        var request = CreateMinimalRequest();

        _mockClient
            .Setup(c => c.DispatchAsync(It.IsAny<JobDistributionRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(workItemId);

        var result = await _sut.DistributeAsync(request, CancellationToken.None);

        result.Success.Should().BeTrue();
        result.WorkItemId.Should().Be(workItemId.ToString());
        // Synchronous dispatch: Queued=false — item is Dispatched immediately, not in Pending queue
        result.Queued.Should().BeFalse("synchronous dispatch always returns Queued=false");
    }

    [Fact]
    public async Task DistributeAsync_When409Conflict_ReturnsFailureResult()
    {
        var request = CreateMinimalRequest();

        _mockClient
            .Setup(c => c.DispatchAsync(It.IsAny<JobDistributionRequest>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("concurrency limit", null, System.Net.HttpStatusCode.Conflict));

        var result = await _sut.DistributeAsync(request, CancellationToken.None);

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("Conflict"); // HttpStatusCode.Conflict formats as "Conflict"
    }

    [Fact]
    public async Task DistributeAsync_When503ServiceUnavailable_ReturnsFailureResult()
    {
        var request = CreateMinimalRequest();

        _mockClient
            .Setup(c => c.DispatchAsync(It.IsAny<JobDistributionRequest>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("no PVC available", null, System.Net.HttpStatusCode.ServiceUnavailable));

        var result = await _sut.DistributeAsync(request, CancellationToken.None);

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("ServiceUnavailable"); // HttpStatusCode.ServiceUnavailable formats as "ServiceUnavailable"
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

/// <summary>Minimal IDbContextFactory for tests — uses InMemory EF.</summary>
file sealed class SimpleDbContextFactory : IDbContextFactory<PipelineDbContext>
{
    private readonly DbContextOptions<PipelineDbContext> _options;
    public SimpleDbContextFactory(DbContextOptions<PipelineDbContext> options) => _options = options;
    public PipelineDbContext CreateDbContext() => new(_options);
}
