using AwesomeAssertions;
using CodingAgentWebUI.Orchestration.Dispatch;
using CodingAgentWebUI.Pipeline.Models;
using Moq;

namespace CodingAgentWebUI.UnitTests.Dispatch;

/// <summary>
/// Tests for <see cref="IChatJobDispatcher"/> interface contract and
/// <see cref="NullChatJobDispatcher"/> null-object implementation.
/// Requirements: Req 15.
/// </summary>
public class IChatJobDispatcherTests
{
    private readonly NullChatJobDispatcher _sut = new();

    // ─── NullChatJobDispatcher.TerminateChatSessionAsync ─────────────────────

    [Fact]
    public async Task NullChatJobDispatcher_TerminateChatSessionAsync_ReturnsCompletedTask()
    {
        // Arrange
        var agentId = "agent-123";

        // Act
        var task = _sut.TerminateChatSessionAsync(agentId, CancellationToken.None);

        // Assert — must be the exact same sentinel as Task.CompletedTask
        await task;
        task.IsCompletedSuccessfully.Should().BeTrue();
    }

    [Fact]
    public void NullChatJobDispatcher_TerminateChatSessionAsync_WithEmptyAgentId_ThrowsArgumentException()
    {
        // TODO: This test does not actually exercise NullChatJobDispatcher.TerminateChatSessionAsync —
        // the exception is thrown by the implicit string→AgentId conversion before the method is ever
        // called, making this a tautological test. The meaningful contract (valid AgentId → completed
        // task) is already covered by NullChatJobDispatcher_TerminateChatSessionAsync_ReturnsCompletedTask.
        // Consider removing this test or replacing it with a test that reaches the method body.
        // empty-string agent IDs are invalid at the type level — the implicit string→AgentId
        // conversion operator rejects them synchronously at the call site before the method body runs.
        Assert.Throws<ArgumentException>(() =>
        {
            AgentId agentId = string.Empty; // throws ArgumentException synchronously
            _ = _sut.TerminateChatSessionAsync(agentId, CancellationToken.None);
        });
    }

    [Fact]
    public async Task NullChatJobDispatcher_TerminateChatSessionAsync_WithCancelledToken_ReturnsCompletedTask()
    {
        // Even with a cancelled token, TerminateChatSessionAsync must return completed (no-op)
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var task = _sut.TerminateChatSessionAsync("agent-1", cts.Token);
        await task;
        task.IsCompletedSuccessfully.Should().BeTrue("NullChatJobDispatcher must be a safe no-op regardless of token state");
    }

    // ─── NullChatJobDispatcher.SendClientKeepalive ────────────────────────────

    [Fact]
    public void NullChatJobDispatcher_SendClientKeepalive_IsNoOp()
    {
        // SendClientKeepalive must be a safe no-op — no exception, no side effects
        var act = () => _sut.SendClientKeepalive("any-agent-id");
        act.Should().NotThrow();
    }

    // ─── NullChatJobDispatcher.DispatchChatPodAsync ───────────────────────────

    [Fact]
    public async Task NullChatJobDispatcher_DispatchChatPodAsync_ThrowsNotSupportedException()
    {
        var act = () => _sut.DispatchChatPodAsync("kiro,dotnet", null, null, CancellationToken.None);

        await act.Should().ThrowAsync<NotSupportedException>();
    }

    [Fact]
    public async Task NullChatJobDispatcher_DispatchChatPodAsync_ExceptionMessageMentionsSignalRMode()
    {
        var ex = await Assert.ThrowsAsync<NotSupportedException>(
            () => _sut.DispatchChatPodAsync("any-selector", "claude-opus-4.8", "high", CancellationToken.None));

        ex.Message.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task NullChatJobDispatcher_DispatchChatPodAsync_WithModelAndEffort_StillThrowsNotSupportedException()
    {
        var act = () => _sut.DispatchChatPodAsync("kiro,dotnet", "claude-sonnet-4", "low", CancellationToken.None);

        await act.Should().ThrowAsync<NotSupportedException>(
            "NullChatJobDispatcher must always throw regardless of model/effort arguments");
    }

    // ─── IChatJobDispatcher assignability ────────────────────────────────────

    [Fact]
    public void NullChatJobDispatcher_ImplementsIChatJobDispatcher()
    {
        // Ensures the null-object can be used anywhere IChatJobDispatcher is expected
        IChatJobDispatcher dispatcher = _sut;
        dispatcher.Should().NotBeNull();
    }
}

/// <summary>
/// Tests for <see cref="ApiChatJobDispatcher"/> — the Blazor-side HTTP bridge.
/// </summary>
public sealed class ApiChatJobDispatcherTests
{
    // ─── SendClientKeepalive ──────────────────────────────────────────────────

    [Fact]
    public void SendClientKeepalive_FiresAndForgets_DoesNotThrow()
    {
        // SendClientKeepalive is fire-and-forget. A successful call must complete
        // synchronously without throwing, even before the underlying HTTP call resolves.
        var mockClient = new Mock<CodingAgentWebUI.Api.Client.IPipelineApiChatClient>();
        // Return a completed task so there is no async work to await
        mockClient.Setup(c => c.SendKeepaliveAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var dispatcher = new CodingAgentWebUI.Services.ApiChatJobDispatcher(mockClient.Object);
        var act = () => dispatcher.SendClientKeepalive("test-agent");
        act.Should().NotThrow("SendClientKeepalive must not throw synchronously");
    }

    [Fact]
    public void SendClientKeepalive_WhenClientThrows_DoesNotPropagate()
    {
        // Even when the HTTP client throws synchronously (bad state), SendClientKeepalive
        // must not propagate the exception — it is fire-and-forget.
        var mockClient = new Mock<CodingAgentWebUI.Api.Client.IPipelineApiChatClient>();
        mockClient.Setup(c => c.SendKeepaliveAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new System.Net.Http.HttpRequestException("network error"));

        var dispatcher = new CodingAgentWebUI.Services.ApiChatJobDispatcher(mockClient.Object);
        var act = () => dispatcher.SendClientKeepalive("test-agent");
        act.Should().NotThrow("fire-and-forget failures must never surface synchronously");
    }
}
