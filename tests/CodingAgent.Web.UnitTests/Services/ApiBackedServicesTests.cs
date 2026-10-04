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
    public async Task DispatchChatPodAsync_Conflict409_ThrowsChatAlreadyActiveException()
    {
        var chatClient = new Mock<IPipelineApiChatClient>();
        chatClient
            .Setup(c => c.DispatchChatPodAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("Conflict", null, System.Net.HttpStatusCode.Conflict));

        var sut = new ApiChatJobDispatcher(chatClient.Object);
        var act = () => sut.DispatchChatPodAsync("kiro", null, null, CancellationToken.None);

        await act.Should().ThrowAsync<ChatAlreadyActiveException>(
            "409 Conflict must be mapped to ChatAlreadyActiveException");
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
        var chatClient = new Mock<IPipelineApiChatClient>();
        chatClient
            .Setup(c => c.DispatchChatPodAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("Gateway Timeout", null, System.Net.HttpStatusCode.GatewayTimeout));

        var sut = new ApiChatJobDispatcher(chatClient.Object);
        var act = () => sut.DispatchChatPodAsync("kiro", null, null, CancellationToken.None);

        await act.Should().ThrowAsync<ChatPodTimeoutException>(
            "504 GatewayTimeout must be mapped to ChatPodTimeoutException");
    }

    [Fact]
    public async Task DispatchChatPodAsync_ChatPodTimeout_HasUnknownTimeoutSeconds()
    {
        var chatClient = new Mock<IPipelineApiChatClient>();
        chatClient
            .Setup(c => c.DispatchChatPodAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("Gateway Timeout", null, System.Net.HttpStatusCode.GatewayTimeout));

        var sut = new ApiChatJobDispatcher(chatClient.Object);

        var ex = await Assert.ThrowsAsync<ChatPodTimeoutException>(
            () => sut.DispatchChatPodAsync("kiro", null, null, CancellationToken.None));

        ex.TimeoutSeconds.Should().Be(-1, "API timeout is unknown — sentinel value -1 is used");
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
