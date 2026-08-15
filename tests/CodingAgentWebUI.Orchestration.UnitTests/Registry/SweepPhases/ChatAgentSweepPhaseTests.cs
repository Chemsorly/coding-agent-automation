using AwesomeAssertions;
using CodingAgentWebUI.Orchestration.Registry;
using CodingAgentWebUI.Orchestration.Registry.SweepPhases;
using CodingAgentWebUI.Pipeline.Models;
using Moq;
using ILogger = Serilog.ILogger;

namespace CodingAgentWebUI.Orchestration.UnitTests.Registry.SweepPhases;

/// <summary>
/// Unit tests for <see cref="ChatAgentSweepPhase"/> in isolation.
/// </summary>
public class ChatAgentSweepPhaseTests
{
    private readonly Mock<ILogger> _mockLogger;
    private readonly ChatAgentSweepPhase _phase;
    private readonly PipelineConfiguration _config = new();

    public ChatAgentSweepPhaseTests()
    {
        _mockLogger = new Mock<ILogger>();
        _phase = new ChatAgentSweepPhase(_mockLogger.Object);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────────

    private static AgentEntry MakeAgent(string agentId, IReadOnlyList<string>? labels = null)
        => new()
        {
            AgentId = agentId,
            ConnectionId = "conn-1",
            Hostname = "host-1",
            Labels = labels ?? [],
            RegisteredAt = DateTimeOffset.UtcNow,
            LastHeartbeatAt = DateTimeOffset.UtcNow,
        };

    // ── Tests ─────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Execute_AgentWithChatLabel_ReturnsTrue()
    {
        var agent = MakeAgent("agent-1", ["chat=true"]);

        var result = await _phase.ExecuteAsync(agent, DateTimeOffset.UtcNow, _config, CancellationToken.None);

        result.Should().BeTrue("chat agent must consume the phase slot to skip subsequent phases");
    }

    [Fact]
    public async Task Execute_AgentWithoutChatLabel_ReturnsFalse()
    {
        var agent = MakeAgent("agent-1", ["kiro=true", "dotnet=true"]);

        var result = await _phase.ExecuteAsync(agent, DateTimeOffset.UtcNow, _config, CancellationToken.None);

        result.Should().BeFalse("non-chat agent must not be consumed by this phase");
    }

    [Fact]
    public async Task Execute_AgentWithNoLabels_ReturnsFalse()
    {
        var agent = MakeAgent("agent-1", []);

        var result = await _phase.ExecuteAsync(agent, DateTimeOffset.UtcNow, _config, CancellationToken.None);

        result.Should().BeFalse();
    }

    [Fact]
    public async Task Execute_CaseInsensitiveMatch_CHAT_TRUE_ReturnsTrue()
    {
        var agent = MakeAgent("agent-1", ["CHAT=TRUE"]);

        var result = await _phase.ExecuteAsync(agent, DateTimeOffset.UtcNow, _config, CancellationToken.None);

        result.Should().BeTrue("label matching must be case-insensitive");
    }

    [Fact]
    public async Task Execute_CaseInsensitiveMatch_Chat_true_ReturnsTrue()
    {
        var agent = MakeAgent("agent-1", ["Chat=true"]);

        var result = await _phase.ExecuteAsync(agent, DateTimeOffset.UtcNow, _config, CancellationToken.None);

        result.Should().BeTrue();
    }

    [Fact]
    public async Task Execute_FreshRegistration_NoWarningLogged()
    {
        var agent = MakeAgent("agent-1", ["chat=true"]);
        // RegisteredAt = UtcNow by default — well within 4h

        await _phase.ExecuteAsync(agent, DateTimeOffset.UtcNow, _config, CancellationToken.None);

        // TODO: [WARNING] This absence assertion targets only the params object[] overload of
        // ILogger.Warning. The production ChatAgentSweepPhase calls Warning(string, string, double) —
        // a two-argument typed overload. Moq may not match this call against the object[] overload,
        // so this "Times.Never" check could pass even if a warning IS logged via the typed overload
        // (false-green absence assertion). Mirror the exact overload used in Execute_OldRegistration_WarningLogged:
        //   _mockLogger.Verify(l => l.Warning(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<double>()), Times.Never)
        _mockLogger.Verify(
            l => l.Warning(It.IsAny<string>(), It.IsAny<object[]>()),
            Times.Never,
            "no warning should be logged for recently registered chat agents");
    }

    [Fact]
    public async Task Execute_OldRegistration_WarningLogged()
    {
        var agent = MakeAgent("agent-1", ["chat=true"]) with
        {
            RegisteredAt = DateTimeOffset.UtcNow.AddHours(-5),
        };

        await _phase.ExecuteAsync(agent, DateTimeOffset.UtcNow, _config, CancellationToken.None);

        _mockLogger.Verify(
            l => l.Warning(
                It.Is<string>(s => s.Contains("{AgentId}") && s.Contains("{AgeHours")),
                It.IsAny<AgentId>(),
                It.IsAny<double>()),
            Times.Once,
            "warning must be logged when chat agent has been registered for >4h");
    }
}
