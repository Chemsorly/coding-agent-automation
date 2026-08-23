using System.Net;
using AwesomeAssertions;
using CodingAgentWebUI.Api.Client;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Services;
using Moq;
using ILogger = Serilog.ILogger;

namespace CodingAgentWebUI.UnitTests.Services;

/// <summary>
/// Unit tests for <see cref="ApiBackedWorkItemFallbackTransitionService"/>.
/// </summary>
public sealed class ApiBackedWorkItemFallbackTransitionServiceTests
{
    private readonly Mock<IPipelineApiWorkItemClient> _client = new();
    private readonly Mock<ILogger> _logger = new();

    private ApiBackedWorkItemFallbackTransitionService CreateSut() =>
        new(_client.Object, _logger.Object);

    private static readonly Guid WorkItemId = Guid.NewGuid();

    // ── Constructor guards ─────────────────────────────────────────────────

    [Fact]
    public void Constructor_NullClient_Throws()
    {
        var act = () => new ApiBackedWorkItemFallbackTransitionService(null!, _logger.Object);
        act.Should().Throw<ArgumentNullException>().WithParameterName("client");
    }

    [Fact]
    public void Constructor_NullLogger_Throws()
    {
        var act = () => new ApiBackedWorkItemFallbackTransitionService(_client.Object, null!);
        act.Should().Throw<ArgumentNullException>().WithParameterName("logger");
    }

    // ── Success path: returns true ─────────────────────────────────────────

    [Fact]
    public async Task TryFallbackChainAsync_Success_ReturnsTrue()
    {
        _client
            .Setup(c => c.PostStatusAsync(WorkItemId, It.IsAny<WorkItemStatusUpdate>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var sut = CreateSut();
        var result = await sut.TryFallbackChainAsync(WorkItemId, WorkItemStatus.Failed, "error", null, CancellationToken.None);

        result.Should().BeTrue();
    }

    [Fact]
    public async Task TryFallbackChainAsync_Success_CallsPostStatusWithCorrectWorkItemId()
    {
        _client
            .Setup(c => c.PostStatusAsync(WorkItemId, It.IsAny<WorkItemStatusUpdate>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var sut = CreateSut();
        await sut.TryFallbackChainAsync(WorkItemId, WorkItemStatus.Succeeded, null, null, CancellationToken.None);

        _client.Verify(c => c.PostStatusAsync(
            WorkItemId,
            It.Is<WorkItemStatusUpdate>(u => u.Status == "Succeeded"),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task TryFallbackChainAsync_WithErrorMessage_PassesItInUpdate()
    {
        WorkItemStatusUpdate? captured = null;
        _client
            .Setup(c => c.PostStatusAsync(WorkItemId, It.IsAny<WorkItemStatusUpdate>(), It.IsAny<CancellationToken>()))
            .Callback<Guid, WorkItemStatusUpdate, CancellationToken>((_, u, _) => captured = u)
            .Returns(Task.CompletedTask);

        var sut = CreateSut();
        await sut.TryFallbackChainAsync(WorkItemId, WorkItemStatus.Failed, "something exploded", FailureReason.AgentError, CancellationToken.None);

        captured.Should().NotBeNull();
        captured!.ErrorMessage.Should().Be("something exploded");
        captured.FailureReason.Should().Be("AgentError");
    }

    [Fact]
    public async Task TryFallbackChainAsync_NullErrorAndReason_PassesNullsInUpdate()
    {
        WorkItemStatusUpdate? captured = null;
        _client
            .Setup(c => c.PostStatusAsync(WorkItemId, It.IsAny<WorkItemStatusUpdate>(), It.IsAny<CancellationToken>()))
            .Callback<Guid, WorkItemStatusUpdate, CancellationToken>((_, u, _) => captured = u)
            .Returns(Task.CompletedTask);

        var sut = CreateSut();
        await sut.TryFallbackChainAsync(WorkItemId, WorkItemStatus.Cancelled, null, null, CancellationToken.None);

        captured!.ErrorMessage.Should().BeNull();
        captured.FailureReason.Should().BeNull();
    }

    // ── 400 Bad Request → returns false (already terminal) ───────────────

    [Fact]
    public async Task TryFallbackChainAsync_Http400_ReturnsFalse()
    {
        _client
            .Setup(c => c.PostStatusAsync(WorkItemId, It.IsAny<WorkItemStatusUpdate>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("bad request", null, HttpStatusCode.BadRequest));

        var sut = CreateSut();
        var result = await sut.TryFallbackChainAsync(WorkItemId, WorkItemStatus.Failed, null, null, CancellationToken.None);

        result.Should().BeFalse("400 = already-terminal or invalid transition — not an error");
    }

    // ── 404 Not Found → returns false (legacy/test run) ──────────────────

    [Fact]
    public async Task TryFallbackChainAsync_Http404_ReturnsFalse()
    {
        _client
            .Setup(c => c.PostStatusAsync(WorkItemId, It.IsAny<WorkItemStatusUpdate>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("not found", null, HttpStatusCode.NotFound));

        var sut = CreateSut();
        var result = await sut.TryFallbackChainAsync(WorkItemId, WorkItemStatus.Failed, null, null, CancellationToken.None);

        result.Should().BeFalse("404 = work item does not exist in API — skip gracefully");
    }

    // ── Other HTTP errors → rethrow for caller retry ──────────────────────

    [Fact]
    public async Task TryFallbackChainAsync_Http500_Rethrows()
    {
        _client
            .Setup(c => c.PostStatusAsync(WorkItemId, It.IsAny<WorkItemStatusUpdate>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("server error", null, HttpStatusCode.InternalServerError));

        var sut = CreateSut();
        var act = async () => await sut.TryFallbackChainAsync(WorkItemId, WorkItemStatus.Failed, null, null, CancellationToken.None);

        await act.Should().ThrowAsync<HttpRequestException>("500 must rethrow for caller retry");
    }

    [Fact]
    public async Task TryFallbackChainAsync_Http503_Rethrows()
    {
        _client
            .Setup(c => c.PostStatusAsync(WorkItemId, It.IsAny<WorkItemStatusUpdate>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("unavailable", null, HttpStatusCode.ServiceUnavailable));

        var sut = CreateSut();
        var act = async () => await sut.TryFallbackChainAsync(WorkItemId, WorkItemStatus.Failed, null, null, CancellationToken.None);

        await act.Should().ThrowAsync<HttpRequestException>("transient errors must be rethrown for caller retry");
    }

    // ── OperationCanceledException propagates unchanged ───────────────────

    [Fact]
    public async Task TryFallbackChainAsync_Cancelled_PropagatesOperationCanceledException()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        _client
            .Setup(c => c.PostStatusAsync(WorkItemId, It.IsAny<WorkItemStatusUpdate>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OperationCanceledException());

        var sut = CreateSut();
        var act = async () => await sut.TryFallbackChainAsync(WorkItemId, WorkItemStatus.Failed, null, null, cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>(
            "cancellation must propagate unchanged, not be swallowed");
    }

    // ── All WorkItemStatus values can be stringified ──────────────────────

    [Theory]
    [InlineData(WorkItemStatus.Failed)]
    [InlineData(WorkItemStatus.Succeeded)]
    [InlineData(WorkItemStatus.Cancelled)]
    [InlineData(WorkItemStatus.Running)]
    public async Task TryFallbackChainAsync_AnyStatus_StatusStringMatchesEnumName(WorkItemStatus status)
    {
        WorkItemStatusUpdate? captured = null;
        _client
            .Setup(c => c.PostStatusAsync(WorkItemId, It.IsAny<WorkItemStatusUpdate>(), It.IsAny<CancellationToken>()))
            .Callback<Guid, WorkItemStatusUpdate, CancellationToken>((_, u, _) => captured = u)
            .Returns(Task.CompletedTask);

        var sut = CreateSut();
        await sut.TryFallbackChainAsync(WorkItemId, status, null, null, CancellationToken.None);

        captured!.Status.Should().Be(status.ToString());
    }
}
