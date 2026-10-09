using AwesomeAssertions;
using CodingAgent.Api.Client;
using CodingAgent.Orchestration.Dispatch;
using CodingAgent.Pipeline.Models;
using CodingAgent.Web.Services;
using Moq;

namespace CodingAgent.Web.UnitTests.Services;

/// <summary>
/// Tests for <see cref="ApiChatJobDispatcher"/>.
/// Uses Moq to avoid needing WireMock in this project.
/// </summary>
public sealed class ApiBackedServicesTests
{
    // ── ApiChatJobDispatcher ──────────────────────────────────────────────

    [Fact]
    public async Task DispatchChatPodAsync_Success_ReturnsAgentId()
    {
        var chatClient = new Mock<IPipelineApiChatClient>();
        chatClient
            .Setup(c => c.DispatchChatPodAsync("kiro,dotnet", null, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync("agent-123");

        var sut = new ApiChatJobDispatcher(chatClient.Object);
        var result = await sut.DispatchChatPodAsync("kiro,dotnet", null, null, CancellationToken.None);

        result.Should().Be("agent-123");
    }

    [Fact]
    public async Task DispatchChatPodAsync_ServiceUnavailable503_ThrowsNoPvcAvailableException()
    {
        var chatClient = new Mock<IPipelineApiChatClient>();
        chatClient
            .Setup(c => c.DispatchChatPodAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("Service Unavailable", null, System.Net.HttpStatusCode.ServiceUnavailable));

        var sut = new ApiChatJobDispatcher(chatClient.Object);
        var act = () => sut.DispatchChatPodAsync("kiro", null, null, CancellationToken.None);

        await act.Should().ThrowAsync<NoPvcAvailableException>(
            "503 ServiceUnavailable must be mapped to NoPvcAvailableException");
    }

    [Fact]
    public async Task DispatchChatPodAsync_GatewayTimeout504_ThrowsChatPodTimeoutException()
    {
        // TODO [WARNING]: This test is an exact duplicate of DispatchChatPodAsync_GatewayTimeoutWithoutApiTimeout_HasZeroTimeoutSeconds
        // below (identical setup, SUT call, and assertion). One of the two should be removed to avoid misleading
        // coverage counts and maintenance confusion — any regression that breaks one breaks both identically.
        var chatClient = new Mock<IPipelineApiChatClient>();
        chatClient
            .Setup(c => c.DispatchChatPodAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("Gateway Timeout", null, System.Net.HttpStatusCode.GatewayTimeout));

        var sut = new ApiChatJobDispatcher(chatClient.Object);

        var ex = await Assert.ThrowsAsync<ChatPodTimeoutException>(
            () => sut.DispatchChatPodAsync("kiro", null, null, CancellationToken.None));

        ex.TimeoutSeconds.Should().Be(0,
            "a plain HttpRequestException has no TimeoutSeconds — the dispatcher uses 0 as the unknown-timeout sentinel");
    }

    [Fact]
    public async Task DispatchChatPodAsync_GatewayTimeoutWithoutApiTimeout_HasZeroTimeoutSeconds()
    {
        // Plain HttpRequestException (not ChatDispatchFailedException) — no TimeoutSeconds available.
        var chatClient = new Mock<IPipelineApiChatClient>();
        chatClient
            .Setup(c => c.DispatchChatPodAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("Gateway Timeout", null, System.Net.HttpStatusCode.GatewayTimeout));

        var sut = new ApiChatJobDispatcher(chatClient.Object);

        var ex = await Assert.ThrowsAsync<ChatPodTimeoutException>(
            () => sut.DispatchChatPodAsync("kiro", null, null, CancellationToken.None));

        ex.TimeoutSeconds.Should().Be(0,
            "a plain 504 HttpRequestException must map to TimeoutSeconds=0, never -1");
    }

    [Fact]
    public async Task DispatchChatPodAsync_GatewayTimeoutWithApiTimeout_PassesTimeoutSeconds()
    {
        // ChatDispatchFailedException carries the API's reported timeout.
        var chatClient = new Mock<IPipelineApiChatClient>();
        chatClient
            .Setup(c => c.DispatchChatPodAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new ChatDispatchFailedException(
                "Chat pod did not connect within 120s.", System.Net.HttpStatusCode.GatewayTimeout, timeoutSeconds: 120));

        var sut = new ApiChatJobDispatcher(chatClient.Object);

        var ex = await Assert.ThrowsAsync<ChatPodTimeoutException>(
            () => sut.DispatchChatPodAsync("kiro", null, null, CancellationToken.None));

        ex.TimeoutSeconds.Should().Be(120,
            "the API's timeout must be forwarded to the UI so the real number of seconds is shown");
    }

    [Fact]
    public async Task DispatchChatPodAsync_ServerErrorWithDetail_PropagatesApiReason()
    {
        // ChatDispatchFailedException with a 500 status is NOT caught by ApiChatJobDispatcher;
        // it must propagate with its original message so the UI can display the API's reason.
        var chatClient = new Mock<IPipelineApiChatClient>();
        chatClient
            .Setup(c => c.DispatchChatPodAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new ChatDispatchFailedException(
                "No template for selector 'kiro'", System.Net.HttpStatusCode.InternalServerError, null));

        var sut = new ApiChatJobDispatcher(chatClient.Object);
        var act = () => sut.DispatchChatPodAsync("kiro", null, null, CancellationToken.None);

        var ex = await act.Should().ThrowAsync<ChatDispatchFailedException>(
            "500-status ChatDispatchFailedException must propagate so the UI can show the API detail");
        ex.WithMessage("No template for selector 'kiro'");
    }

    [Fact]
    public async Task DispatchChatPodAsync_OtherHttpError_Propagates()
    {
        var chatClient = new Mock<IPipelineApiChatClient>();
        chatClient
            .Setup(c => c.DispatchChatPodAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("Internal Server Error", null, System.Net.HttpStatusCode.InternalServerError));

        var sut = new ApiChatJobDispatcher(chatClient.Object);
        var act = () => sut.DispatchChatPodAsync("kiro", null, null, CancellationToken.None);

        // 500 is not remapped — should propagate as HttpRequestException
        await act.Should().ThrowAsync<HttpRequestException>(
            "non-remapped status codes must propagate as-is");
    }

    [Fact]
    public async Task TerminateChatSessionAsync_DelegatesToClient()
    {
        var chatClient = new Mock<IPipelineApiChatClient>();
        chatClient
            .Setup(c => c.TerminateChatSessionAsync("agent-abc", It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var sut = new ApiChatJobDispatcher(chatClient.Object);
        await sut.TerminateChatSessionAsync(new AgentId { Value = "agent-abc" }, CancellationToken.None);

        chatClient.Verify(c => c.TerminateChatSessionAsync("agent-abc", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task DispatchChatPodAsync_WithModelAndEffort_PassesThrough()
    {
        var chatClient = new Mock<IPipelineApiChatClient>();
        chatClient
            .Setup(c => c.DispatchChatPodAsync("kiro,dotnet", "claude-4", "high", It.IsAny<CancellationToken>()))
            .ReturnsAsync("agent-xyz");

        var sut = new ApiChatJobDispatcher(chatClient.Object);
        var result = await sut.DispatchChatPodAsync("kiro,dotnet", "claude-4", "high", CancellationToken.None);

        result.Should().Be("agent-xyz");
        chatClient.Verify(c => c.DispatchChatPodAsync("kiro,dotnet", "claude-4", "high", It.IsAny<CancellationToken>()), Times.Once);
    }
}
