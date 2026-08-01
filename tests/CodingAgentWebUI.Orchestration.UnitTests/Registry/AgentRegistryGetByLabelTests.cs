using AwesomeAssertions;
using CodingAgentWebUI.Orchestration.Registry;
using CodingAgentWebUI.Pipeline.Models;
using Moq;
using Serilog;

namespace CodingAgentWebUI.Orchestration.UnitTests.Registry;

/// <summary>
/// Unit tests for <see cref="AgentRegistryService.GetAgentsByLabel"/>.
/// Requirements: Req 8 — filter registered agents by label key=value pair,
/// case-insensitive, exact match semantics.
/// </summary>
public class AgentRegistryGetByLabelTests
{
    private static AgentRegistryService CreateRegistry() =>
        new AgentRegistryService(Mock.Of<ILogger>());

    private static AgentEntry RegisterAgent(
        AgentRegistryService registry,
        string agentId,
        IReadOnlyList<string> labels,
        AgentStatus status = AgentStatus.Idle)
    {
        var message = new AgentRegistrationMessage
        {
            AgentId = agentId,
            Hostname = "host-1",
            Labels = labels,
            ActiveJob = null
        };
        var entry = registry.Register(message, connectionId: $"conn-{agentId}");
        if (status != AgentStatus.Idle)
            registry.TransitionStatus(agentId, status);
        return entry;
    }

    // ── Agent with matching label returned ────────────────────────────────

    [Fact]
    public void GetAgentsByLabel_AgentHasMatchingLabel_ReturnsAgent()
    {
        var registry = CreateRegistry();
        RegisterAgent(registry, "agent-1", ["env=prod", "kiro=true"]);

        var result = registry.GetAgentsByLabel("env", "prod");

        result.Should().HaveCount(1);
        result[0].AgentId.Should().Be("agent-1");
    }

    // ── Agent without label not returned ─────────────────────────────────

    [Fact]
    public void GetAgentsByLabel_AgentLacksLabel_NotReturned()
    {
        var registry = CreateRegistry();
        RegisterAgent(registry, "agent-1", ["kiro=true", "dotnet=true"]);

        var result = registry.GetAgentsByLabel("env", "prod");

        result.Should().BeEmpty();
    }

    // ── Multiple agents — only matching returned ──────────────────────────

    [Fact]
    public void GetAgentsByLabel_MultipleAgents_OnlyMatchingReturned()
    {
        var registry = CreateRegistry();
        RegisterAgent(registry, "agent-match", ["env=prod", "kiro=true"]);
        RegisterAgent(registry, "agent-no-match", ["kiro=true"]);

        var result = registry.GetAgentsByLabel("env", "prod");

        result.Should().HaveCount(1);
        result[0].AgentId.Should().Be("agent-match");
    }

    [Fact]
    public void GetAgentsByLabel_MultipleAgentsAllMatch_AllReturned()
    {
        var registry = CreateRegistry();
        RegisterAgent(registry, "agent-1", ["env=prod", "kiro=true"]);
        RegisterAgent(registry, "agent-2", ["env=prod", "dotnet=true"]);
        RegisterAgent(registry, "agent-3", ["env=staging"]);

        var result = registry.GetAgentsByLabel("env", "prod");

        result.Should().HaveCount(2);
        result.Select(a => a.AgentId).Should().Contain(["agent-1", "agent-2"]);
    }

    // ── Exact match: "env=prod" does NOT match "env=production" ──────────

    [Fact]
    public void GetAgentsByLabel_ValueIsPrefixOfAnotherValue_NotReturned()
    {
        var registry = CreateRegistry();
        RegisterAgent(registry, "agent-1", ["env=production"]);

        var result = registry.GetAgentsByLabel("env", "prod");

        result.Should().BeEmpty("\"env=prod\" must not match \"env=production\"");
    }

    [Fact]
    public void GetAgentsByLabel_ExactValueMatch_Returned()
    {
        var registry = CreateRegistry();
        RegisterAgent(registry, "agent-exact", ["env=prod"]);
        RegisterAgent(registry, "agent-prefix", ["env=production"]);

        var result = registry.GetAgentsByLabel("env", "prod");

        result.Should().HaveCount(1);
        result[0].AgentId.Should().Be("agent-exact");
    }

    // ── Empty labels list → not returned ─────────────────────────────────

    [Fact]
    public void GetAgentsByLabel_AgentHasEmptyLabelsList_NotReturned()
    {
        var registry = CreateRegistry();
        RegisterAgent(registry, "agent-empty-labels", []);

        var result = registry.GetAgentsByLabel("env", "prod");

        result.Should().BeEmpty();
    }

    // ── GUID-format value matched correctly ──────────────────────────────

    [Fact]
    public void GetAgentsByLabel_GuidFormatValue_MatchedCorrectly()
    {
        var registry = CreateRegistry();
        var dispatchId = Guid.NewGuid();
        RegisterAgent(registry, "agent-chat", [$"chat-session-id={dispatchId}"]);

        var result = registry.GetAgentsByLabel("chat-session-id", dispatchId.ToString());

        result.Should().HaveCount(1);
        result[0].AgentId.Should().Be("agent-chat");
    }

    [Fact]
    public void GetAgentsByLabel_DifferentGuid_NotReturned()
    {
        var registry = CreateRegistry();
        RegisterAgent(registry, "agent-chat", [$"chat-session-id={Guid.NewGuid()}"]);

        var result = registry.GetAgentsByLabel("chat-session-id", Guid.NewGuid().ToString());

        result.Should().BeEmpty();
    }

    // ── Case-insensitive: "Chat=true" matches GetAgentsByLabel("chat", "true") ──

    [Fact]
    public void GetAgentsByLabel_LabelKeyDifferentCase_MatchesCaseInsensitively()
    {
        var registry = CreateRegistry();
        // Agent registers with "Chat=true" (mixed case key)
        RegisterAgent(registry, "agent-chat", ["Chat=true", "kiro=true"]);

        // Query with lowercase key
        var result = registry.GetAgentsByLabel("chat", "true");

        result.Should().HaveCount(1);
        result[0].AgentId.Should().Be("agent-chat");
    }

    [Fact]
    public void GetAgentsByLabel_LabelValueDifferentCase_MatchesCaseInsensitively()
    {
        var registry = CreateRegistry();
        // Agent registers with uppercase value
        RegisterAgent(registry, "agent-chat", ["chat=TRUE"]);

        // Query with lowercase value
        var result = registry.GetAgentsByLabel("chat", "true");

        result.Should().HaveCount(1);
        result[0].AgentId.Should().Be("agent-chat");
    }

    [Fact]
    public void GetAgentsByLabel_BothKeyAndValueDifferentCase_MatchesCaseInsensitively()
    {
        var registry = CreateRegistry();
        // Exact case from design doc: "Chat=true" registered, query with "chat","true"
        RegisterAgent(registry, "agent-mixed-case", ["Chat=true", "env=prod"]);

        var result = registry.GetAgentsByLabel("chat", "true");

        result.Should().HaveCount(1);
        result[0].AgentId.Should().Be("agent-mixed-case");
    }

    // ── No agents registered → empty result ──────────────────────────────

    [Fact]
    public void GetAgentsByLabel_NoAgentsRegistered_ReturnsEmpty()
    {
        var registry = CreateRegistry();

        var result = registry.GetAgentsByLabel("env", "prod");

        result.Should().BeEmpty();
    }

    // ── Return type contract ──────────────────────────────────────────────

    [Fact]
    public void GetAgentsByLabel_ReturnsIReadOnlyList()
    {
        var registry = CreateRegistry();

        // The method must compile with return type IReadOnlyList<AgentEntry>
        IReadOnlyList<AgentEntry> result = registry.GetAgentsByLabel("env", "prod");

        result.Should().NotBeNull();
    }
}
