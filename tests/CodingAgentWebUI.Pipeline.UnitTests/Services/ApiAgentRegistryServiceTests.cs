using AwesomeAssertions;
using CodingAgentWebUI.Api.Client;
using CodingAgentWebUI.Orchestration.Registry;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Services;
using Microsoft.Extensions.Time.Testing;
using Moq;
using ILogger = Serilog.ILogger;

namespace CodingAgentWebUI.Pipeline.UnitTests.Services;

/// <summary>
/// Tests for ApiAgentRegistryService.
/// Covers: snapshot TTL/staleness, all read paths, write no-ops, RefreshAsync, constructor guards.
/// </summary>
public sealed class ApiAgentRegistryServiceTests
{
    private readonly Mock<IPipelineApiAgentClient> _client = new();
    private readonly Mock<ILogger> _logger = new();

    private static AgentEntry MakeEntry(string id = "agent-1", AgentStatus status = AgentStatus.Idle) =>
        new()
        {
            AgentId = new AgentId(id),
            ConnectionId = $"conn-{id}",
            Hostname = "host",
            Labels = [],
            RegisteredAt = DateTimeOffset.UtcNow,
            Status = status
        };

    private ApiAgentRegistryService Create(TimeProvider? clock = null) =>
        new(_client.Object, clock ?? TimeProvider.System, _logger.Object);

    // ── Constructor guards ────────────────────────────────────────────────

    [Fact]
    public void Constructor_NullClient_Throws()
    {
        var act = () => new ApiAgentRegistryService(null!, TimeProvider.System, _logger.Object);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Constructor_NullClock_Throws()
    {
        var act = () => new ApiAgentRegistryService(_client.Object, null!, _logger.Object);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Constructor_NullLogger_Throws()
    {
        var act = () => new ApiAgentRegistryService(_client.Object, TimeProvider.System, null!);
        act.Should().Throw<ArgumentNullException>();
    }

    // ── Before first refresh — all reads return empty ─────────────────────

    [Fact]
    public void GetAllAgents_BeforeRefresh_ReturnsEmpty()
    {
        var svc = Create();
        svc.GetAllAgents().Should().BeEmpty();
    }

    [Fact]
    public void GetIdleAgents_BeforeRefresh_ReturnsEmpty()
    {
        var svc = Create();
        svc.GetIdleAgents().Should().BeEmpty();
    }

    [Fact]
    public void GetBusyAgentCount_BeforeRefresh_ReturnsZero()
    {
        var svc = Create();
        svc.GetBusyAgentCount().Should().Be(0);
    }

    [Fact]
    public void GetByAgentId_BeforeRefresh_ReturnsNull()
    {
        var svc = Create();
        svc.GetByAgentId(new AgentId("a1")).Should().BeNull();
    }

    [Fact]
    public void GetByConnectionId_BeforeRefresh_ReturnsNull()
    {
        var svc = Create();
        svc.GetByConnectionId("conn-1").Should().BeNull();
    }

    [Fact]
    public void LastRefreshedAt_BeforeRefresh_IsNull()
    {
        var svc = Create();
        svc.LastRefreshedAt.Should().BeNull();
    }

    // ── After refresh — reads serve snapshot ──────────────────────────────

    [Fact]
    public async Task GetAllAgents_AfterRefresh_ReturnsFetchedList()
    {
        var agent = MakeEntry("a1");
        _client.Setup(c => c.GetAgentsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<AgentEntry> { agent } as IReadOnlyList<AgentEntry>);

        var clock = new FakeTimeProvider();
        var svc = Create(clock);
        await svc.RefreshAsync();

        svc.GetAllAgents().Should().HaveCount(1);
    }

    [Fact]
    public async Task GetIdleAgents_FiltersToIdle()
    {
        _client.Setup(c => c.GetAgentsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<AgentEntry>
            {
                MakeEntry("a1", AgentStatus.Idle),
                MakeEntry("a2", AgentStatus.Busy),
                MakeEntry("a3", AgentStatus.Idle)
            } as IReadOnlyList<AgentEntry>);

        var svc = Create();
        await svc.RefreshAsync();

        svc.GetIdleAgents().Should().HaveCount(2);
    }

    [Fact]
    public async Task GetBusyAgentCount_CountsBusy()
    {
        _client.Setup(c => c.GetAgentsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<AgentEntry>
            {
                MakeEntry("a1", AgentStatus.Busy),
                MakeEntry("a2", AgentStatus.Busy),
                MakeEntry("a3", AgentStatus.Idle)
            } as IReadOnlyList<AgentEntry>);

        var svc = Create();
        await svc.RefreshAsync();

        svc.GetBusyAgentCount().Should().Be(2);
    }

    [Fact]
    public async Task GetByAgentId_ReturnsMatchingEntry()
    {
        var agent = MakeEntry("a1");
        _client.Setup(c => c.GetAgentsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<AgentEntry> { agent } as IReadOnlyList<AgentEntry>);

        var svc = Create();
        await svc.RefreshAsync();

        var result = svc.GetByAgentId(new AgentId("a1"));
        result.Should().NotBeNull();
        result!.AgentId.Value.Should().Be("a1");
    }

    [Fact]
    public async Task GetByAgentId_NotFound_ReturnsNull()
    {
        _client.Setup(c => c.GetAgentsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<AgentEntry> { MakeEntry("a1") } as IReadOnlyList<AgentEntry>);

        var svc = Create();
        await svc.RefreshAsync();

        svc.GetByAgentId(new AgentId("a-missing")).Should().BeNull();
    }

    [Fact]
    public async Task GetByConnectionId_ReturnsMatchingEntry()
    {
        var agent = MakeEntry("a1");
        _client.Setup(c => c.GetAgentsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<AgentEntry> { agent } as IReadOnlyList<AgentEntry>);

        var svc = Create();
        await svc.RefreshAsync();

        var result = svc.GetByConnectionId("conn-a1");
        result.Should().NotBeNull();
    }

    [Fact]
    public async Task LastRefreshedAt_AfterRefresh_IsSet()
    {
        _client.Setup(c => c.GetAgentsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<AgentEntry>() as IReadOnlyList<AgentEntry>);

        var svc = Create();
        await svc.RefreshAsync();

        svc.LastRefreshedAt.Should().NotBeNull();
    }

    // ── Snapshot staleness ────────────────────────────────────────────────

    [Fact]
    public async Task GetAllAgents_AfterSnapshotExpiry_ReturnsEmpty()
    {
        var agent = MakeEntry("a1");
        _client.Setup(c => c.GetAgentsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<AgentEntry> { agent } as IReadOnlyList<AgentEntry>);

        var clock = new FakeTimeProvider();
        var svc = Create(clock);
        svc.MaxSnapshotAge = TimeSpan.FromSeconds(10);
        await svc.RefreshAsync();

        // Advance past the TTL
        clock.Advance(TimeSpan.FromSeconds(11));

        svc.GetAllAgents().Should().BeEmpty();
    }

    [Fact]
    public async Task GetAllAgents_WithinSnapshotTtl_ReturnsData()
    {
        var agent = MakeEntry("a1");
        _client.Setup(c => c.GetAgentsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<AgentEntry> { agent } as IReadOnlyList<AgentEntry>);

        var clock = new FakeTimeProvider();
        var svc = Create(clock);
        svc.MaxSnapshotAge = TimeSpan.FromSeconds(30);
        await svc.RefreshAsync();

        clock.Advance(TimeSpan.FromSeconds(5)); // within TTL

        svc.GetAllAgents().Should().HaveCount(1);
    }

    [Fact]
    public async Task GetAllAgents_AfterSuccessfulReRefresh_ReturnsNewData()
    {
        // First refresh: 1 agent
        _client.SetupSequence(c => c.GetAgentsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<AgentEntry> { MakeEntry("a1") } as IReadOnlyList<AgentEntry>)
            .ReturnsAsync(new List<AgentEntry> { MakeEntry("a1"), MakeEntry("a2") } as IReadOnlyList<AgentEntry>);

        var svc = Create();
        await svc.RefreshAsync();
        await svc.RefreshAsync(); // second refresh

        svc.GetAllAgents().Should().HaveCount(2);
    }

    // ── Write no-ops ──────────────────────────────────────────────────────

    [Fact]
    public void Register_ReturnsEntryWithCorrectFields_DoesNotPersist()
    {
        var svc = Create();
        var msg = new AgentRegistrationMessage
        {
            AgentId = new AgentId("a1"),
            Hostname = "host1",
            Labels = ["kiro"],
            ActiveJob = null
        };

        var result = svc.Register(msg, "conn-1");

        result.AgentId.Value.Should().Be("a1");
        result.Hostname.Should().Be("host1");

        // Not persisted — snapshot stays empty
        svc.GetAllAgents().Should().BeEmpty();
    }

    [Fact]
    public void Deregister_ReturnsFalse()
    {
        var svc = Create();
        svc.Deregister(new AgentId("a1")).Should().BeFalse();
    }

    [Fact]
    public void UpdateHeartbeat_DoesNotThrow()
    {
        var svc = Create();
        var act = () => svc.UpdateHeartbeat(new AgentId("a1"), DateTimeOffset.UtcNow);
        act.Should().NotThrow();
    }

    [Fact]
    public void TransitionStatus_DoesNotThrow()
    {
        var svc = Create();
        var act = () => svc.TransitionStatus(new AgentId("a1"), AgentStatus.Busy);
        act.Should().NotThrow();
    }

    // ── Input guards ──────────────────────────────────────────────────────

    [Fact]
    public void GetByAgentId_EmptyValue_Throws()
    {
        var svc = Create();
        var act = () => svc.GetByAgentId(new AgentId(""));
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void GetByConnectionId_Empty_Throws()
    {
        var svc = Create();
        var act = () => svc.GetByConnectionId("");
        act.Should().Throw<ArgumentException>();
    }
}
