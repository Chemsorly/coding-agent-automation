using AwesomeAssertions;
using CodingAgentWebUI.Agent;

namespace CodingAgentWebUI.Agent.UnitTests;

/// <summary>
/// Unit tests for <see cref="AgentJobSlotManager.GetChatSlotSnapshot"/>.
/// Tests the public API directly without reflection.
/// </summary>
// TODO: These tests only verify sequential scenarios. Add a concurrent stress test
// (e.g., GetChatSlotSnapshot in a loop while another thread calls ReleaseChatSlot)
// to validate the atomicity guarantee that is the method's primary purpose.
public class AgentJobSlotManagerChatSnapshotTests
{
    [Fact]
    public void GetChatSlotSnapshot_WhenNoChatActive_ReturnsNulls()
    {
        var slotManager = CreateSlotManager();

        var (sessionId, task) = slotManager.GetChatSlotSnapshot();

        sessionId.Should().BeNull();
        task.Should().BeNull();
    }

    [Fact]
    public void GetChatSlotSnapshot_AfterAcquireChatSlot_ReturnsSessionId()
    {
        var slotManager = CreateSlotManager();
        slotManager.TryAcquireChatSlot("session-1", out _);

        var (sessionId, task) = slotManager.GetChatSlotSnapshot();

        sessionId.Should().Be("session-1");
        // Task is not yet set — SetActiveChatTask is called after Task.Run
        task.Should().BeNull();
    }

    [Fact]
    public void GetChatSlotSnapshot_AfterSetActiveChatTask_ReturnsBothFields()
    {
        var slotManager = CreateSlotManager();
        slotManager.TryAcquireChatSlot("session-1", out _);
        var chatTask = Task.CompletedTask;
        slotManager.SetActiveChatTask(chatTask);

        var (sessionId, task) = slotManager.GetChatSlotSnapshot();

        sessionId.Should().Be("session-1");
        task.Should().BeSameAs(chatTask);
    }

    [Fact]
    public void GetChatSlotSnapshot_AfterReleaseChatSlot_ReturnsNullSessionId()
    {
        var slotManager = CreateSlotManager();
        slotManager.TryAcquireChatSlot("session-1", out _);
        var chatTask = Task.CompletedTask;
        slotManager.SetActiveChatTask(chatTask);

        slotManager.ReleaseChatSlot();

        var (sessionId, task) = slotManager.GetChatSlotSnapshot();

        sessionId.Should().BeNull();
        // TODO: This asserts an implementation detail — _activeChatTask is never cleared by
        // ReleaseChatSlot(), leaving a stale reference. If ReleaseChatSlot is later improved to
        // clear the task field, this assertion should be updated. Callers should check SessionId
        // (not Task) to determine if a chat is active.
        // _activeChatTask is never cleared by ReleaseChatSlot — stale reference remains
        task.Should().BeSameAs(chatTask);
    }

    [Fact]
    public void GetChatSlotSnapshot_WhenJobIsActive_ReturnsNullChatState()
    {
        var slotManager = CreateSlotManager();
        slotManager.TryAcquireJobSlot("job-1", out _);

        var (sessionId, task) = slotManager.GetChatSlotSnapshot();

        sessionId.Should().BeNull();
        task.Should().BeNull();
    }

    private static AgentJobSlotManager CreateSlotManager()
    {
        return new AgentJobSlotManager(() => Task.CompletedTask);
    }
}
