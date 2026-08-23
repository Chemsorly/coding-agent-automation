using AwesomeAssertions;
using CodingAgentWebUI.Hub;
using CodingAgentWebUI.Orchestration.Registry;
using CodingAgentWebUI.Pipeline.Models;

namespace CodingAgentWebUI.Pipeline.UnitTests.Services;

/// <summary>
/// Tests for AgentHub.Chat.cs — covers ValidateChatSessionOwnership (pure static logic).
/// SignalR hub methods (ReportChatResponse, ReportChatCompleted) are not unit-testable
/// without a full IHubContext mock wiring — they are covered by E2E tests.
/// </summary>
public sealed class AgentHubChatTests
{
    private static AgentEntry MakeAgent(string agentId = "agent-1", string? activeSession = null)
    {
        var entry = new AgentEntry
        {
            AgentId = new AgentId(agentId),
            ConnectionId = $"conn-{agentId}",
            Hostname = "test-host",
            Labels = [],
            RegisteredAt = DateTimeOffset.UtcNow
        };
        entry.ActiveChatSessionId = activeSession;
        return entry;
    }

    // ── ValidateChatSessionOwnership ──────────────────────────────────────

    [Fact]
    public void ValidateChatSessionOwnership_WhenSessionMatches_ReturnsValid()
    {
        var agent = MakeAgent(activeSession: "session-42");

        var (isValid, agentId) = AgentHub.ValidateChatSessionOwnership(agent, "session-42");

        isValid.Should().BeTrue();
        agentId.Should().Be("agent-1");
    }

    [Fact]
    public void ValidateChatSessionOwnership_WhenSessionMismatch_ReturnsInvalid()
    {
        var agent = MakeAgent(activeSession: "session-42");

        var (isValid, agentId) = AgentHub.ValidateChatSessionOwnership(agent, "other-session");

        isValid.Should().BeFalse();
        agentId.Should().Be("agent-1");
    }

    [Fact]
    public void ValidateChatSessionOwnership_WhenNullAgent_ReturnsInvalid()
    {
        var (isValid, agentId) = AgentHub.ValidateChatSessionOwnership(null, "session-1");

        isValid.Should().BeFalse();
        agentId.Should().Be("unknown");
    }

    [Fact]
    public void ValidateChatSessionOwnership_WhenAgentHasNoActiveSession_ReturnsInvalid()
    {
        var agent = MakeAgent(activeSession: null);

        var (isValid, _) = AgentHub.ValidateChatSessionOwnership(agent, "any-session");

        isValid.Should().BeFalse();
    }

    [Theory]
    [InlineData("session-1", "session-1", true)]
    [InlineData("session-1", "session-2", false)]
    [InlineData(null, "session-1", false)]
    [InlineData("session-1", "SESSION-1", false)] // case-sensitive
    public void ValidateChatSessionOwnership_Theory(string? agentSession, string requestedSession, bool expectedValid)
    {
        var agent = MakeAgent(activeSession: agentSession);

        var (isValid, _) = AgentHub.ValidateChatSessionOwnership(agent, requestedSession);

        isValid.Should().Be(expectedValid);
    }
}
