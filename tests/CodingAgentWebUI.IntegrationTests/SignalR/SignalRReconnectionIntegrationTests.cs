using AwesomeAssertions;
using CodingAgentWebUI.Pipeline.Models;
using Microsoft.AspNetCore.SignalR.Client;

namespace CodingAgentWebUI.IntegrationTests.SignalR;

/// <summary>
/// Integration tests verifying SignalR reconnection behavior:
/// identity preservation, job continuity, stale recovery, and no duplicate registrations.
/// </summary>
public sealed class SignalRReconnectionIntegrationTests : IClassFixture<SignalRTestFixture>, IAsyncLifetime
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(10);

    private readonly SignalRTestFixture _fixture;

    public SignalRReconnectionIntegrationTests(SignalRTestFixture fixture)
    {
        _fixture = fixture;
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync()
    {
        _fixture.Registry.Reset();
        return Task.CompletedTask;
    }

    [Fact]
    public async Task Reconnection_PreservesAgentId_UpdatesConnectionId()
    {
        using var cts = new CancellationTokenSource(TestTimeout);

        // Arrange: connect and register agent
        const string agentId = "reconnect-agent-1";
        await using var conn1 = _fixture.CreateHubConnection(agentId);
        await conn1.StartAsync(cts.Token);
        await RegisterAgentAsync(conn1, agentId, cts.Token);

        var entry1 = _fixture.Registry.GetByAgentId(agentId);
        entry1.Should().NotBeNull();
        var originalConnectionId = entry1!.ConnectionId;

        // Act: disconnect, then reconnect with same agentId
        await conn1.StopAsync();
        await _fixture.WaitForStatusAsync(agentId, AgentStatus.Disconnected);

        await using var conn2 = _fixture.CreateHubConnection(agentId);
        await conn2.StartAsync(cts.Token);
        await RegisterAgentAsync(conn2, agentId, cts.Token);

        // Assert: single agent entry with updated ConnectionId
        var agents = _fixture.Registry.GetAllAgents();
        agents.Should().HaveCount(1);

        var entry2 = _fixture.Registry.GetByAgentId(agentId);
        entry2.Should().NotBeNull();
        entry2!.AgentId.Should().Be(agentId);
        entry2.ConnectionId.Should().NotBe(originalConnectionId);
        entry2.Status.Should().Be(AgentStatus.Idle);
    }

    [Fact]
    public async Task StaleDetection_ReconnectionHealsStatus()
    {
        using var cts = new CancellationTokenSource(TestTimeout);

        // Arrange: connect and register agent
        const string agentId = "stale-agent-1";
        await using var conn1 = _fixture.CreateHubConnection(agentId);
        await conn1.StartAsync(cts.Token);
        await RegisterAgentAsync(conn1, agentId, cts.Token);

        // Simulate stale detection: set heartbeat far in past and transition to Disconnected
        var entry = _fixture.Registry.GetByAgentId(agentId)!;
        entry.LastHeartbeatAt = DateTimeOffset.UtcNow.AddMinutes(-10);
        _fixture.Registry.TransitionStatus(agentId, AgentStatus.Disconnected);

        entry.Status.Should().Be(AgentStatus.Disconnected);

        // Act: disconnect real connection, then reconnect and re-register
        await conn1.StopAsync();

        await using var conn2 = _fixture.CreateHubConnection(agentId);
        await conn2.StartAsync(cts.Token);
        await RegisterAgentAsync(conn2, agentId, cts.Token);

        // Assert: agent recovered to Idle
        var healed = _fixture.Registry.GetByAgentId(agentId)!;
        healed.Status.Should().Be(AgentStatus.Idle);
        healed.DisconnectedAt.Should().BeNull();
    }

    [Fact]
    public async Task InProgressJob_NotRedispatched_DuringBriefDisconnection()
    {
        using var cts = new CancellationTokenSource(TestTimeout);

        // Arrange: connect two agents, assign job to agent-1
        const string agent1Id = "job-agent-1";
        const string agent2Id = "job-agent-2";
        const string jobId = "test-job-123";

        await using var conn1 = _fixture.CreateHubConnection(agent1Id);
        await using var conn2 = _fixture.CreateHubConnection(agent2Id);
        await conn1.StartAsync(cts.Token);
        await conn2.StartAsync(cts.Token);
        await RegisterAgentAsync(conn1, agent1Id, cts.Token);
        await RegisterAgentAsync(conn2, agent2Id, cts.Token);

        // Simulate job assignment by setting ActiveJobId directly
        var entry1 = _fixture.Registry.GetByAgentId(agent1Id)!;
        entry1.ActiveJobId = jobId;
        entry1.Status = AgentStatus.Busy;

        // Act: disconnect agent-1 (brief network blip)
        await conn1.StopAsync();
        await _fixture.WaitForStatusAsync(agent1Id, AgentStatus.Disconnected);

        // Assert: job remains on agent-1, agent-2 has no job
        var disconnectedEntry = _fixture.Registry.GetByAgentId(agent1Id)!;
        disconnectedEntry.ActiveJobId.Should().Be(jobId);

        var agent2Entry = _fixture.Registry.GetByAgentId(agent2Id)!;
        agent2Entry.ActiveJobId.Should().BeNull();
        agent2Entry.Status.Should().Be(AgentStatus.Idle);

        // Act: reconnect agent-1 and re-register
        await using var conn1Reconnected = _fixture.CreateHubConnection(agent1Id);
        await conn1Reconnected.StartAsync(cts.Token);
        await RegisterAgentAsync(conn1Reconnected, agent1Id, cts.Token);

        // Assert: agent-1 restored to Busy with same job (AddOrUpdate preserves ActiveJobId)
        var reconnectedEntry = _fixture.Registry.GetByAgentId(agent1Id)!;
        reconnectedEntry.Status.Should().Be(AgentStatus.Busy);
        reconnectedEntry.ActiveJobId.Should().Be(jobId);
    }

    [Fact]
    public async Task Reconnection_NoDuplicateAgentRecords()
    {
        using var cts = new CancellationTokenSource(TestTimeout);

        // Arrange: connect and register
        const string agentId = "dedup-agent-1";
        await using var conn1 = _fixture.CreateHubConnection(agentId);
        await conn1.StartAsync(cts.Token);
        await RegisterAgentAsync(conn1, agentId, cts.Token);

        _fixture.Registry.GetAllAgents().Should().HaveCount(1);

        // Act: disconnect and reconnect multiple times
        await conn1.StopAsync();
        await _fixture.WaitForStatusAsync(agentId, AgentStatus.Disconnected);

        await using var conn2 = _fixture.CreateHubConnection(agentId);
        await conn2.StartAsync(cts.Token);
        await RegisterAgentAsync(conn2, agentId, cts.Token);

        await conn2.StopAsync();
        await _fixture.WaitForStatusAsync(agentId, AgentStatus.Disconnected);

        await using var conn3 = _fixture.CreateHubConnection(agentId);
        await conn3.StartAsync(cts.Token);
        await RegisterAgentAsync(conn3, agentId, cts.Token);

        // Assert: still exactly 1 agent
        _fixture.Registry.GetAllAgents().Should().HaveCount(1);
        _fixture.Registry.GetByAgentId(agentId)!.Status.Should().Be(AgentStatus.Idle);
    }

    private static Task RegisterAgentAsync(HubConnection connection, string agentId, CancellationToken ct = default)
    {
        return connection.InvokeAsync("RegisterAgent", new AgentRegistrationMessage
        {
            AgentId = agentId,
            Hostname = "test-host",
            Labels = ["dotnet"]
        }, ct);
    }
}
