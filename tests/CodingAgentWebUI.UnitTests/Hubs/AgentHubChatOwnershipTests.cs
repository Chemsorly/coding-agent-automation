using AwesomeAssertions;
using CodingAgentWebUI.Hub;
using CodingAgentWebUI.Pipeline.Models;

namespace CodingAgentWebUI.UnitTests.Hubs;

/// <summary>
/// Unit tests for <see cref="AgentHub.ValidateChatSessionOwnership"/>.
/// Pure logic — no I/O, no mocks required.
/// </summary>
public sealed class AgentHubChatOwnershipTests
{
    private static AgentEntry CreateAgent(string agentId, string? activeChatSessionId) => new()
    {
        AgentId = agentId,
        ConnectionId = "conn-test",
        Hostname = "k8s-pod",
        Labels = [],
        RegisteredAt = DateTimeOffset.UtcNow,
        ActiveChatSessionId = activeChatSessionId,
    };

    [Fact]
    public void ValidateChatSessionOwnership_AgentOwnsSession_ReturnsValid()
    {
        var agent = CreateAgent("agent-1", "session-abc");
        var (isValid, agentId) = AgentHub.ValidateChatSessionOwnership(agent, "session-abc");

        isValid.Should().BeTrue();
        agentId.Should().Be("agent-1");
    }

    [Fact]
    public void ValidateChatSessionOwnership_AgentHasDifferentSession_ReturnsInvalid()
    {
        var agent = CreateAgent("agent-1", "session-other");
        var (isValid, agentId) = AgentHub.ValidateChatSessionOwnership(agent, "session-abc");

        isValid.Should().BeFalse();
        agentId.Should().Be("agent-1");
    }

    [Fact]
    public void ValidateChatSessionOwnership_AgentHasNullSession_ReturnsInvalid()
    {
        var agent = CreateAgent("agent-1", null);
        var (isValid, agentId) = AgentHub.ValidateChatSessionOwnership(agent, "session-abc");

        isValid.Should().BeFalse();
        agentId.Should().Be("agent-1");
    }

    [Fact]
    public void ValidateChatSessionOwnership_NullAgent_ReturnsFalseWithUnknownId()
    {
        var (isValid, agentId) = AgentHub.ValidateChatSessionOwnership(null, "session-abc");

        isValid.Should().BeFalse();
        agentId.Should().Be("unknown");
    }

    [Fact]
    public void ValidateChatSessionOwnership_ReturnsCorrectAgentId()
    {
        var agent = CreateAgent("my-specific-agent", "session-xyz");
        var (isValid, agentId) = AgentHub.ValidateChatSessionOwnership(agent, "session-xyz");

        isValid.Should().BeTrue();
        agentId.Should().Be("my-specific-agent");
    }
}
