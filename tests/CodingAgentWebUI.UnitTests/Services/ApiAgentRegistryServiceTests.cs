using AwesomeAssertions;
using CodingAgentWebUI.Api.Client;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Services;
using Microsoft.Extensions.Time.Testing;
using Moq;
using ILogger = Serilog.ILogger;

namespace CodingAgentWebUI.UnitTests.Services;

/// <summary>
/// Tests for <see cref="ApiAgentRegistryService"/> — the monolith's read-only view of the agent
/// registry the Pipeline API owns.
///
/// <para>
/// The behaviour that matters here is the seam between an asynchronous data source and a
/// synchronous interface. <c>IAgentRegistryService.GetAllAgents()</c> returns a list, not a task,
/// and Blazor render paths call it, so the read path must never touch the network. These tests pin
/// that down from both sides: reads serve a snapshot without calling the client at all, and a
/// snapshot that has gone stale is dropped rather than rendered as if it were live.
/// </para>
/// </summary>
public sealed class ApiAgentRegistryServiceTests
{
    private static readonly DateTimeOffset Origin = new(2026, 8, 20, 12, 0, 0, TimeSpan.Zero);

    private static AgentEntry Agent(
        string id,
        AgentStatus status = AgentStatus.Idle,
        string? connectionId = null,
        string? activeJobId = null) => new()
        {
            AgentId = new AgentId(id),
            ConnectionId = connectionId ?? $"conn-{id}",
            Hostname = $"host-{id}",
            Labels = new List<string> { "dotnet" },
            Status = status,
            ActiveJobId = activeJobId,
            RegisteredAt = Origin,
            LastHeartbeatAt = Origin
        };

    private static (ApiAgentRegistryService Registry, Mock<IPipelineApiAgentClient> Client, FakeTimeProvider Clock)
        Build(params AgentEntry[] agents)
    {
        var clock = new FakeTimeProvider(Origin);
        var client = new Mock<IPipelineApiAgentClient>();
        client.Setup(c => c.GetAgentsAsync(It.IsAny<CancellationToken>()))
              .ReturnsAsync(agents.ToList());

        var registry = new ApiAgentRegistryService(client.Object, clock, new Mock<ILogger>().Object);
        return (registry, client, clock);
    }

    // ── Before the first refresh ────────────────────────────────────────────

    /// <summary>
    /// Constructing the service must not, by itself, claim knowledge of the cluster. The poller has
    /// not run yet, so "I don't know" has to read as empty rather than as a fabricated list.
    /// </summary>
    [Fact]
    public void BeforeFirstRefresh_ReadsReturnEmpty_AndNeverCallTheClient()
    {
        var (registry, client, _) = Build(Agent("a1"));

        registry.GetAllAgents().Should().BeEmpty();
        registry.GetIdleAgents().Should().BeEmpty();
        registry.GetBusyAgentCount().Should().Be(0);
        registry.GetByAgentId(new AgentId("a1")).Should().BeNull();
        registry.GetByConnectionId("conn-a1").Should().BeNull();
        registry.LastRefreshedAt.Should().BeNull();

        client.Verify(c => c.GetAgentsAsync(It.IsAny<CancellationToken>()), Times.Never,
            "a synchronous read must never reach for the network — that is the sync-over-async trap "
            + "this class exists to avoid");
    }

    // ── After a refresh ─────────────────────────────────────────────────────

    [Fact]
    public async Task AfterRefresh_GetAllAgents_ReturnsWhatTheApiReported()
    {
        var (registry, _, _) = Build(Agent("a1"), Agent("a2", AgentStatus.Busy, activeJobId: "job-1"));

        await registry.RefreshAsync();

        registry.GetAllAgents().Should().HaveCount(2);
        registry.GetAllAgents().Select(a => a.AgentId.Value).Should().BeEquivalentTo("a1", "a2");
        registry.LastRefreshedAt.Should().Be(Origin);
    }

    [Fact]
    public async Task AfterRefresh_LookupsResolveByAgentIdAndConnectionId()
    {
        var (registry, _, _) = Build(Agent("a1", connectionId: "conn-x"));

        await registry.RefreshAsync();

        var byAgentId = registry.GetByAgentId(new AgentId("a1"));
        byAgentId.Should().NotBeNull();
        byAgentId!.Hostname.Should().Be("host-a1");
        registry.GetByConnectionId("conn-x").Should().NotBeNull();
        registry.GetByAgentId(new AgentId("nope")).Should().BeNull();
        registry.GetByConnectionId("nope").Should().BeNull();
    }

    [Fact]
    public async Task AfterRefresh_IdleAndBusyProjections_MatchAgentStatus()
    {
        var (registry, _, _) = Build(
            Agent("idle-1"),
            Agent("idle-2"),
            Agent("busy-1", AgentStatus.Busy),
            Agent("gone-1", AgentStatus.Disconnected));

        await registry.RefreshAsync();

        registry.GetIdleAgents().Select(a => a.AgentId.Value).Should().BeEquivalentTo("idle-1", "idle-2");
        registry.GetBusyAgentCount().Should().Be(1);
        registry.GetAllAgents().Should().HaveCount(4,
            "GetAllAgents is status-agnostic — the monitoring page renders Disconnected agents too");
    }

    /// <summary>
    /// The whole point of the snapshot: many reads, one fetch. <c>AgentMonitoring.razor</c> calls
    /// <c>GetAllAgents()</c> during render and re-renders every five seconds.
    /// </summary>
    [Fact]
    public async Task Reads_AreServedFromTheSnapshot_WithoutAdditionalApiCalls()
    {
        var (registry, client, _) = Build(Agent("a1"), Agent("a2", AgentStatus.Busy));

        await registry.RefreshAsync();

        for (var i = 0; i < 25; i++)
        {
            registry.GetAllAgents();
            registry.GetIdleAgents();
            registry.GetBusyAgentCount();
            registry.GetByAgentId(new AgentId("a1"));
        }

        client.Verify(c => c.GetAgentsAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    // ── Staleness ───────────────────────────────────────────────────────────

    [Fact]
    public async Task SnapshotWithinMaxAge_IsStillServed()
    {
        var (registry, _, clock) = Build(Agent("a1"));
        registry.MaxSnapshotAge = TimeSpan.FromSeconds(30);

        await registry.RefreshAsync();
        clock.Advance(TimeSpan.FromSeconds(29));

        registry.GetAllAgents().Should().ContainSingle();
    }

    /// <summary>
    /// A snapshot past its age is discarded, not served. Callers such as <c>IssueDrawerService</c>
    /// gate dispatch on <c>GetAllAgents().Count == 0</c>, so serving agents that may have vanished
    /// minutes ago would green-light work no one is left to run.
    /// </summary>
    [Fact]
    public async Task SnapshotOlderThanMaxAge_IsDroppedAndReadsReturnEmpty()
    {
        var (registry, _, clock) = Build(Agent("a1"));
        registry.MaxSnapshotAge = TimeSpan.FromSeconds(30);

        await registry.RefreshAsync();
        clock.Advance(TimeSpan.FromSeconds(31));

        registry.GetAllAgents().Should().BeEmpty();
        registry.GetIdleAgents().Should().BeEmpty();
        registry.GetBusyAgentCount().Should().Be(0);
        registry.GetByAgentId(new AgentId("a1")).Should().BeNull();
        registry.GetByConnectionId("conn-a1").Should().BeNull();
    }

    /// <summary>
    /// One failed poll must not blank the UI — that is what the age window buys. The previous
    /// snapshot keeps serving until it genuinely ages out.
    /// </summary>
    [Fact]
    public async Task FailedRefresh_LeavesThePreviousSnapshotInPlace()
    {
        var clock = new FakeTimeProvider(Origin);
        var client = new Mock<IPipelineApiAgentClient>();
        client.SetupSequence(c => c.GetAgentsAsync(It.IsAny<CancellationToken>()))
              .ReturnsAsync(new List<AgentEntry> { Agent("a1") })
              .ThrowsAsync(new HttpRequestException("api down"));

        var registry = new ApiAgentRegistryService(client.Object, clock, new Mock<ILogger>().Object);
        registry.MaxSnapshotAge = TimeSpan.FromSeconds(30);

        await registry.RefreshAsync();
        clock.Advance(TimeSpan.FromSeconds(5));

        var act = async () => await registry.RefreshAsync();
        await act.Should().ThrowAsync<HttpRequestException>(
            "the poller owns failure logging, so RefreshAsync must not swallow the error");

        registry.GetAllAgents().Should().ContainSingle("the last good snapshot is still within MaxSnapshotAge");

        clock.Advance(TimeSpan.FromSeconds(26));
        registry.GetAllAgents().Should().BeEmpty("a sustained outage must stop showing ghosts");
    }

    [Fact]
    public async Task Refresh_ReplacesTheSnapshotRatherThanMergingIntoIt()
    {
        var clock = new FakeTimeProvider(Origin);
        var client = new Mock<IPipelineApiAgentClient>();
        client.SetupSequence(c => c.GetAgentsAsync(It.IsAny<CancellationToken>()))
              .ReturnsAsync(new List<AgentEntry> { Agent("a1"), Agent("a2") })
              .ReturnsAsync(new List<AgentEntry> { Agent("a2") });

        var registry = new ApiAgentRegistryService(client.Object, clock, new Mock<ILogger>().Object);

        await registry.RefreshAsync();
        await registry.RefreshAsync();

        registry.GetAllAgents().Select(a => a.AgentId.Value).Should().BeEquivalentTo("a2");
        registry.GetByAgentId(new AgentId("a1")).Should().BeNull(
            "an agent the API no longer reports has disconnected — it must not linger in the index");
    }

    // ── Writes are not owned by this process ────────────────────────────────

    /// <summary>
    /// Only <c>AgentHub.RegisterAgent</c> in the Pipeline API writes the registry. The mutators here
    /// exist to satisfy the interface; if one silently took effect, the monolith would show agents
    /// the cluster does not have.
    /// </summary>
    [Fact]
    public async Task Register_DoesNotAddToTheSnapshot()
    {
        var (registry, _, _) = Build(Agent("real-1"));
        await registry.RefreshAsync();

        var result = registry.Register(
            new AgentRegistrationMessage
            {
                AgentId = new AgentId("ghost-1"),
                Hostname = "ghost-host",
                Labels = ["dotnet"]
            },
            "ghost-conn");

        result.AgentId.Value.Should().Be("ghost-1", "the signature demands a non-null entry back");
        registry.GetAllAgents().Select(a => a.AgentId.Value).Should().BeEquivalentTo("real-1");
        registry.GetByAgentId(new AgentId("ghost-1")).Should().BeNull();
    }

    [Fact]
    public async Task Deregister_ReturnsFalse_AndLeavesTheSnapshotUntouched()
    {
        var (registry, _, _) = Build(Agent("a1"));
        await registry.RefreshAsync();

        registry.Deregister(new AgentId("a1")).Should().BeFalse();
        registry.GetAllAgents().Should().ContainSingle();
    }

    [Fact]
    public async Task TransitionStatusAndHeartbeat_AreNoOps()
    {
        var (registry, _, _) = Build(Agent("a1"));
        await registry.RefreshAsync();

        registry.TransitionStatus(new AgentId("a1"), AgentStatus.Busy);
        registry.UpdateHeartbeat(new AgentId("a1"), Origin.AddMinutes(5));

        var agent = registry.GetByAgentId(new AgentId("a1"));
        agent.Should().NotBeNull();
        agent!.Status.Should().Be(AgentStatus.Idle, "status is owned by the Pipeline API");
        registry.GetBusyAgentCount().Should().Be(0);
    }

    // ── Argument validation ─────────────────────────────────────────────────

    [Fact]
    public void GetByAgentId_WithDefaultAgentId_Throws()
    {
        var (registry, _, _) = Build();

        var act = () => registry.GetByAgentId(default);

        act.Should().Throw<ArgumentException>(
            "default(AgentId) carries a null Value and is a sentinel, never a lookup key");
    }
}
