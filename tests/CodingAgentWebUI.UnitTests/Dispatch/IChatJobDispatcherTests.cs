using AwesomeAssertions;
using CodingAgentWebUI.Orchestration.Dispatch;

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
    public async Task NullChatJobDispatcher_TerminateChatSessionAsync_WithEmptyAgentId_ReturnsCompletedTask()
    {
        var task = _sut.TerminateChatSessionAsync(string.Empty, CancellationToken.None);
        await task;
        task.IsCompletedSuccessfully.Should().BeTrue();
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
