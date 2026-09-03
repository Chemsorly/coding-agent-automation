using AwesomeAssertions;
using CodingAgentWebUI.Api;
using CodingAgentWebUI.Orchestration.Dispatch;
using CodingAgentWebUI.Pipeline.Models;
using Microsoft.AspNetCore.Http.HttpResults;
using Moq;
using Xunit;

namespace CodingAgentWebUI.Api.IntegrationTests;

/// <summary>
/// Unit tests for <see cref="ChatEndpoints"/> static handler methods.
/// Calls the internal static methods directly to avoid needing a full host.
/// </summary>
[Collection(ApiIntegrationTestCollection.Name)]
public sealed class ChatEndpointsTests
{
    // ─── ChatKeepalive ────────────────────────────────────────────────────────

    [Fact]
    public void ChatKeepalive_CallsSendClientKeepalive_ReturnsOk()
    {
        var mockDispatcher = new Mock<IChatJobDispatcher>();
        const string agentId = "caa-chat-abc123";

        var result = ChatEndpoints.ChatKeepalive(agentId, mockDispatcher.Object);

        result.Result.Should().BeOfType<Ok>("keepalive must return 200 for a valid agentId");
        mockDispatcher.Verify(d => d.SendClientKeepalive(agentId), Times.Once,
            "dispatcher.SendClientKeepalive must be called with the agentId from the route");
    }

    [Fact]
    public void ChatKeepalive_UnknownAgentId_StillReturnsOk()
    {
        // SendClientKeepalive is idempotent — unknown agentIds are no-ops on the server
        var mockDispatcher = new Mock<IChatJobDispatcher>();
        mockDispatcher.Setup(d => d.SendClientKeepalive(It.IsAny<string>()));

        var result = ChatEndpoints.ChatKeepalive("unknown-agent", mockDispatcher.Object);

        result.Result.Should().BeOfType<Ok>("keepalive must return 200 even for unknown sessions");
    }

    [Theory]
    [InlineData("../etc/passwd")]
    [InlineData("agent@host")]
    [InlineData("Agent-1")]
    [InlineData("UPPERCASE")]
    [InlineData("has space")]
    [InlineData("")]
    public void ChatKeepalive_InvalidAgentId_Returns400AndDoesNotCallDispatcher(string invalidAgentId)
    {
        var mockDispatcher = new Mock<IChatJobDispatcher>();

        var result = ChatEndpoints.ChatKeepalive(invalidAgentId, mockDispatcher.Object);

        result.Result.Should().BeOfType<BadRequest>(
            $"agentId '{invalidAgentId}' contains characters outside [a-z0-9_.-] and must return 400");
        mockDispatcher.Verify(d => d.SendClientKeepalive(It.IsAny<string>()), Times.Never,
            "dispatcher must not be called when agentId is invalid");
    }

    [Theory]
    [InlineData("caa-chat-abc123")]
    [InlineData("agent.1_test")]
    [InlineData("my-agent")]
    [InlineData("a")]
    public void ChatKeepalive_ValidAgentId_Returns200AndCallsDispatcher(string validAgentId)
    {
        var mockDispatcher = new Mock<IChatJobDispatcher>();

        var result = ChatEndpoints.ChatKeepalive(validAgentId, mockDispatcher.Object);

        result.Result.Should().BeOfType<Ok>(
            $"agentId '{validAgentId}' is valid and must return 200");
        mockDispatcher.Verify(d => d.SendClientKeepalive(validAgentId), Times.Once,
            "dispatcher must be called for valid agentId");
    }

    // ─── TerminateChatSession ─────────────────────────────────────────────────

    [Fact]
    public async Task TerminateChatSession_CallsTerminate_ReturnsOk()
    {
        var mockDispatcher = new Mock<IChatJobDispatcher>();
        mockDispatcher.Setup(d => d.TerminateChatSessionAsync(It.IsAny<AgentId>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        const string agentId = "caa-chat-xyz789";

        var result = await ChatEndpoints.TerminateChatSession(agentId, mockDispatcher.Object, CancellationToken.None);

        result.Should().BeOfType<Ok>("terminate must always return 200 (idempotent)");
        mockDispatcher.Verify(d => d.TerminateChatSessionAsync(
            It.Is<AgentId>(id => id.Value == agentId),
            It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task TerminateChatSession_UnknownAgent_StillReturnsOk()
    {
        var mockDispatcher = new Mock<IChatJobDispatcher>();
        mockDispatcher.Setup(d => d.TerminateChatSessionAsync(It.IsAny<AgentId>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var result = await ChatEndpoints.TerminateChatSession("no-such-agent", mockDispatcher.Object, CancellationToken.None);

        result.Should().BeOfType<Ok>("terminate is idempotent — unknown agentId is a no-op");
    }

    // ─── DispatchChatPod ──────────────────────────────────────────────────────

    [Fact]
    public async Task DispatchChatPod_Success_ReturnsOkWithAgentId()
    {
        var mockDispatcher = new Mock<IChatJobDispatcher>();
        mockDispatcher.Setup(d => d.DispatchChatPodAsync("kiro,dotnet", null, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync("caa-chat-success1");

        var request = new ChatEndpoints.DispatchChatPodRequest("kiro,dotnet", null, null);
        var result = await ChatEndpoints.DispatchChatPod(request, mockDispatcher.Object, CancellationToken.None);

        result.Result.Should().BeOfType<Ok<ChatEndpoints.DispatchChatPodResponse>>();
        var ok = (Ok<ChatEndpoints.DispatchChatPodResponse>)result.Result;
        ok.Value!.AgentId.Should().Be("caa-chat-success1");
    }

    [Fact]
    public async Task DispatchChatPod_NoPvcAvailable_Returns503()
    {
        var mockDispatcher = new Mock<IChatJobDispatcher>();
        mockDispatcher.Setup(d => d.DispatchChatPodAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new NoPvcAvailableException());

        var request = new ChatEndpoints.DispatchChatPodRequest("kiro,dotnet", null, null);
        var result = await ChatEndpoints.DispatchChatPod(request, mockDispatcher.Object, CancellationToken.None);

        result.Result.Should().BeOfType<StatusCodeHttpResult>();
        var statusResult = (StatusCodeHttpResult)result.Result;
        statusResult.StatusCode.Should().Be(503);
    }

    [Fact]
    public async Task DispatchChatPod_PodConnectTimeout_Returns504()
    {
        var mockDispatcher = new Mock<IChatJobDispatcher>();
        mockDispatcher.Setup(d => d.DispatchChatPodAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new ChatPodTimeoutException(120));

        var request = new ChatEndpoints.DispatchChatPodRequest("kiro,dotnet", null, null);
        var result = await ChatEndpoints.DispatchChatPod(request, mockDispatcher.Object, CancellationToken.None);

        result.Result.Should().BeOfType<StatusCodeHttpResult>();
        var statusResult = (StatusCodeHttpResult)result.Result;
        statusResult.StatusCode.Should().Be(504);
    }
}
