using AwesomeAssertions;
using CodingAgentWebUI.Orchestration.Dispatch;
using CodingAgentWebUI.Orchestration.Registry;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using Moq;
using ILogger = Serilog.ILogger;

namespace CodingAgentWebUI.Pipeline.UnitTests.Services;

/// <summary>
/// Tests for JobDeduplicationGuardService.SelectAgent.
/// Covers: no agents, no compatible agents, FIFO ordering, label matching, disabled agent skip,
/// race condition double-check, null guards.
/// </summary>
public sealed class JobDeduplicationGuardServiceTests
{
    private readonly Mock<IAgentRegistryService> _registry = new();
    private readonly Mock<ILogger> _logger = new();
    private readonly JobDeduplicationGuardService _sut;

    public JobDeduplicationGuardServiceTests()
    {
        _sut = new JobDeduplicationGuardService(_registry.Object, _logger.Object);
    }

    private static AgentEntry MakeAgent(
        string id = "a1",
        AgentStatus status = AgentStatus.Idle,
        string[] labels = null!,
        bool disabled = false,
        DateTimeOffset? lastCompleted = null) =>
        new()
        {
            AgentId = new AgentId(id),
            ConnectionId = $"conn-{id}",
            Hostname = "host",
            Labels = labels ?? ["kiro"],
            RegisteredAt = DateTimeOffset.UtcNow.AddMinutes(-10),
            Status = status,
            Disabled = disabled,
            LastJobCompletedAt = lastCompleted
        };

    // ── Constructor guards ────────────────────────────────────────────────

    [Fact]
    public void Constructor_NullRegistry_Throws()
    {
        var act = () => new JobDeduplicationGuardService(null!, _logger.Object);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Constructor_NullLogger_Throws()
    {
        var act = () => new JobDeduplicationGuardService(_registry.Object, null!);
        act.Should().Throw<ArgumentNullException>();
    }

    // ── No agents ─────────────────────────────────────────────────────────

    [Fact]
    public void SelectAgent_WhenNoIdleAgents_ReturnsNull()
    {
        _registry.Setup(r => r.GetIdleAgents()).Returns([]);

        var result = _sut.SelectAgent(["kiro"]);
        result.Should().BeNull();
    }

    // ── Label matching ────────────────────────────────────────────────────

    [Fact]
    public void SelectAgent_WhenNoCompatibleLabels_ReturnsNull()
    {
        var agent = MakeAgent(labels: ["dotnet"]);
        _registry.Setup(r => r.GetIdleAgents()).Returns([agent]);

        var result = _sut.SelectAgent(["kiro"]);
        result.Should().BeNull();
    }

    [Fact]
    public void SelectAgent_WhenLabelsMatch_ReturnsAndMarksBusy()
    {
        var agent = MakeAgent(labels: ["kiro", "dotnet"]);
        _registry.Setup(r => r.GetIdleAgents()).Returns([agent]);

        var result = _sut.SelectAgent(["kiro"]);

        result.Should().NotBeNull();
        result!.Status.Should().Be(AgentStatus.Busy);
        result.BusySince.Should().NotBeNull();
    }

    [Fact]
    public void SelectAgent_EmptyRequiredLabels_AnyAgentMatches()
    {
        var agent = MakeAgent(labels: ["kiro"]);
        _registry.Setup(r => r.GetIdleAgents()).Returns([agent]);

        var result = _sut.SelectAgent([]);
        result.Should().NotBeNull();
    }

    // ── FIFO ordering ─────────────────────────────────────────────────────

    [Fact]
    public void SelectAgent_MultipleCandidates_SelectsLongestIdle()
    {
        var older = MakeAgent("a1", labels: ["kiro"],
            lastCompleted: DateTimeOffset.UtcNow.AddMinutes(-10));
        var newer = MakeAgent("a2", labels: ["kiro"],
            lastCompleted: DateTimeOffset.UtcNow.AddMinutes(-1));

        _registry.Setup(r => r.GetIdleAgents()).Returns([newer, older]);

        var result = _sut.SelectAgent(["kiro"]);
        result!.AgentId.Value.Should().Be("a1"); // older completer = FIFO first
    }

    [Fact]
    public void SelectAgent_FallsBackToRegisteredAt_WhenNoLastCompleted()
    {
        // Create entries with different RegisteredAt via init
        var first = new AgentEntry
        {
            AgentId = new AgentId("a1"),
            ConnectionId = "c1",
            Hostname = "h",
            Labels = ["kiro"],
            RegisteredAt = DateTimeOffset.UtcNow.AddMinutes(-20),
            Status = AgentStatus.Idle
        };
        var second = new AgentEntry
        {
            AgentId = new AgentId("a2"),
            ConnectionId = "c2",
            Hostname = "h",
            Labels = ["kiro"],
            RegisteredAt = DateTimeOffset.UtcNow.AddMinutes(-5),
            Status = AgentStatus.Idle
        };

        _registry.Setup(r => r.GetIdleAgents()).Returns([second, first]);

        var result = _sut.SelectAgent(["kiro"]);
        result!.AgentId.Value.Should().Be("a1"); // registered first = FIFO
    }

    // ── Disabled agent ────────────────────────────────────────────────────

    [Fact]
    public void SelectAgent_DisabledAgent_IsSkipped()
    {
        var disabled = MakeAgent("a1", labels: ["kiro"], disabled: true);
        _registry.Setup(r => r.GetIdleAgents()).Returns([disabled]);

        var result = _sut.SelectAgent(["kiro"]);
        result.Should().BeNull();
    }

    [Fact]
    public void SelectAgent_DisabledAndEnabledAgent_ReturnsEnabledOnly()
    {
        var disabled = MakeAgent("a1", labels: ["kiro"], disabled: true,
            lastCompleted: DateTimeOffset.UtcNow.AddMinutes(-10));
        var enabled = MakeAgent("a2", labels: ["kiro"], disabled: false,
            lastCompleted: DateTimeOffset.UtcNow.AddMinutes(-1));

        _registry.Setup(r => r.GetIdleAgents()).Returns([disabled, enabled]);

        var result = _sut.SelectAgent(["kiro"]);
        result!.AgentId.Value.Should().Be("a2");
    }

    // ── Race condition: agent becomes non-Idle between snapshot and lock ──

    [Fact]
    public void SelectAgent_AgentStatusChangedToNonIdle_BeforeReservation_ReturnsNull()
    {
        // Agent is Idle in the registry snapshot but status already changed (simulated via
        // setting Status directly — the real race happens in concurrent environments)
        var agent = MakeAgent("a1", labels: ["kiro"]);
        agent.Status = AgentStatus.Busy; // changed between snapshot and lock

        _registry.Setup(r => r.GetIdleAgents()).Returns([agent]);

        // Since GetIdleAgents returns the entry with current Status=Busy,
        // the double-check inside the lock will skip it
        var result = _sut.SelectAgent(["kiro"]);
        result.Should().BeNull();
    }

    // ── Null guard ────────────────────────────────────────────────────────

    [Fact]
    public void SelectAgent_NullRequiredLabels_Throws()
    {
        var act = () => _sut.SelectAgent(null!);
        act.Should().Throw<ArgumentNullException>();
    }
}
