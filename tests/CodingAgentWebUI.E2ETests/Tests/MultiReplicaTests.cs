using CodingAgentWebUI.E2ETests.Fakes;
using CodingAgentWebUI.E2ETests.Infrastructure;
using CodingAgentWebUI.Orchestration;
using CodingAgentWebUI.Orchestration.Registry;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using Microsoft.Extensions.DependencyInjection;

namespace CodingAgentWebUI.E2ETests.Tests;

/// <summary>
/// Multi-replica correctness tests. Two <see cref="ApiE2EWebApplicationFactory"/> instances share
/// one <see cref="CodingAgentWebUI.TestUtilities.FakeRedisStore"/>, exercising the distributed
/// service layer (<see cref="DistributedAgentRegistryService"/>,
/// <see cref="CodingAgentWebUI.Orchestration.DistributedRunService"/>,
/// <see cref="CodingAgentWebUI.Orchestration.Dispatch.AgentReservationService"/>) across replica
/// boundaries without Docker.
///
/// <para>
/// <b>Known gap:</b> Lua script atomicity is not simulated. <c>RemoveRun</c> uses a single Lua
/// SREM + EXPIREAT on real Redis; in <see cref="CodingAgentWebUI.TestUtilities.FakeRedisStore"/>
/// these execute as two non-atomic operations. Tests that assert on <c>RemoveRun</c> behavior
/// therefore validate sequential correctness, not concurrent exclusivity.
/// </para>
/// </summary>
[Trait("Category", "E2E")]
[Trait("Feature", "MultiReplica")]
[Collection(MultiReplicaE2ECollection.Name)]
public sealed class MultiReplicaTests : MultiReplicaTestBase
{
    public MultiReplicaTests(MultiReplicaE2EFixture fixture) : base(fixture) { }

    // ── Helper ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Registers an agent directly against a registry instance (no hub connection).
    /// Used in tests that assert on state layer behavior, not hub wiring.
    /// </summary>
    private static AgentEntry RegisterAgent(
        IAgentRegistryService registry,
        string agentId,
        string connectionId,
        params string[] labels)
    {
        var message = new AgentRegistrationMessage
        {
            AgentId = new AgentId(agentId),
            Hostname = "test-host",
            Labels = labels,
            ActiveJob = null
        };
        return registry.Register(message, connectionId);
    }

    // ── R1: Agent registered on Replica1 is visible via Replica2 ──────────

    [Fact]
    public async Task MultiReplica_AgentRegisteredOnReplica1_VisibleFromReplica2_GetIdleAgents()
    {
        // Arrange: register agent on Replica1's registry instance
        var agentId = $"mr-agent-{Guid.NewGuid():N}";
        RegisterAgent(Fixture.Registry1, agentId, $"conn-{agentId}", "dotnet", "kiro");

        // Allow async FakeRedisStore write to settle (all ops are synchronous/in-memory,
        // but belt-and-suspenders given the async interface)
        await Task.Delay(50);

        // Act: query Replica2's registry — the shared FakeRedisStore is the source of truth
        var idleAgents = Fixture.Registry2.GetIdleAgents();

        // Assert: the specific agent registered on Replica1 appears in Replica2's idle list
        // with its labels intact after the hash round-trip.
        var registered = idleAgents.Single(a => a.AgentId.Value == agentId);
        Assert.Contains("dotnet", registered.Labels);
    }

    // ── R2: GetAllAgents cross-replica ────────────────────────────────────

    [Fact]
    public async Task MultiReplica_AgentRegisteredOnReplica1_VisibleFromReplica2_GetAllAgents()
    {
        var agentId = $"mr-all-{Guid.NewGuid():N}";
        RegisterAgent(Fixture.Registry1, agentId, $"conn-{agentId}");

        await Task.Delay(50);

        var all = Fixture.Registry2.GetAllAgents();
        Assert.Contains(all, a => a.AgentId.Value == agentId);
    }

    // ── R3: UpdateAgentFieldAsync cross-replica ───────────────────────────

    [Fact]
    public async Task MultiReplica_UpdateAgentField_VisibleAcrossReplicas()
    {
        // Arrange: register agent on Replica1 and set ActiveJobId via UpdateAgentFieldAsync
        var agentId = $"mr-field-{Guid.NewGuid():N}";
        RegisterAgent(Fixture.Registry1, agentId, $"conn-{agentId}");

        var jobId = Guid.NewGuid().ToString("N");
        // Field name must match the camelCase key used in AgentEntryToHashEntries / HashToEntry.
        await Fixture.Registry1.UpdateAgentFieldAsync(new AgentId(agentId), "activeJobId", jobId);

        // Act: read back from Replica2
        var entry = Fixture.Registry2.GetByAgentId(new AgentId(agentId));

        // Assert
        Assert.NotNull(entry);
        Assert.Equal(jobId, entry.ActiveJobId);
    }

    [Fact]
    public async Task MultiReplica_RunAddedOnReplica1_VisibleFromReplica2()
    {
        // Arrange
        var runId = Guid.NewGuid().ToString("N");
        var run = new PipelineRun
        {
            RunId = runId,
            IssueIdentifier = $"test-org/test-repo#42",
            IssueTitle = "Multi-replica run test",
            IssueProviderConfigId = "issue-e2e",
            RepoProviderConfigId = "repo-e2e",
        };

        // Act: add run through Replica1
        Fixture.RunService1.AddRun(run);

        await Task.Yield();

        // Assert: readable via Replica2
        var retrieved = Fixture.RunService2.GetRun(new RunId(runId));
        Assert.NotNull(retrieved);
        Assert.Equal(runId, retrieved.RunId);
        Assert.Equal("Multi-replica run test", retrieved.IssueTitle);
    }

    // ── R6: RemoveRun on Replica1 — second call on Replica2 returns null ──

    [Fact]
    public async Task MultiReplica_RemoveRun_SecondCallReturnsNull()
    {
        // Arrange: add run via Replica1
        var runId = Guid.NewGuid().ToString("N");
        Fixture.RunService1.AddRun(new PipelineRun
        {
            RunId = runId,
            IssueIdentifier = $"test-org/test-repo#99",
            IssueTitle = "RemoveRun cross-replica",
            IssueProviderConfigId = "issue-e2e",
            RepoProviderConfigId = "repo-e2e",
        });

        await Task.Yield();

        // Act: remove from Replica1 first, then attempt from Replica2
        var first = Fixture.RunService1.RemoveRun(new RunId(runId));
        var second = Fixture.RunService2.RemoveRun(new RunId(runId));

        // Assert: first removal wins, second returns null (SREM returned 0)
        Assert.NotNull(first);
        Assert.Null(second);

        // After RemoveRun, the run is removed from the active set on both replicas.
        // Note: the hash key (run:{id}) is retained in Redis with a 5-minute TTL for async
        // completion tracking — GetRun may still return the run during that window. The
        // meaningful idempotency guarantee is that neither replica counts the run as active.
        Assert.False(Fixture.RunService1.IsIssueBeingProcessed(
            new IssueIdentifier($"test-org/test-repo#99"), new ProviderConfigId("issue-e2e")));
    }

    // ── R7: MarkRecentlyCompleted cross-replica ───────────────────────────

    [Fact]
    public async Task MultiReplica_MarkRecentlyCompleted_VisibleAcrossReplicas()
    {
        // Arrange
        var issueIdentifier = new IssueIdentifier($"test-org/test-repo#{Guid.NewGuid():N}");
        var providerConfigId = new ProviderConfigId("issue-e2e");

        // Precondition: not yet marked on either replica
        Assert.False(Fixture.RunService1.WasRecentlyCompleted(issueIdentifier, providerConfigId));
        Assert.False(Fixture.RunService2.WasRecentlyCompleted(issueIdentifier, providerConfigId));

        // Act: mark completed via Replica1
        Fixture.RunService1.MarkRecentlyCompleted(issueIdentifier, providerConfigId);

        await Task.Yield();

        // Assert: Replica2 sees it as recently completed
        Assert.True(Fixture.RunService2.WasRecentlyCompleted(issueIdentifier, providerConfigId));
    }

    // ── Failover tests ─────────────────────────────────────────────────────
    //
    // Pattern: write state through Replica1, dispose Replica1 (simulate pod death),
    // start a fresh Replica1b pointing at the same FakeRedisStore, assert Replica1b
    // and Replica2 both see the state written by the now-dead Replica1.
    //
    // This validates the fundamental guarantee of the distributed architecture:
    // state lives in Redis, not in the pod. A new pod is a full replacement — it
    // re-reads everything from the store, with no warm-up period and no stale data.

    /// <summary>
    /// Spins up a replacement factory that shares <see cref="MultiReplicaE2EFixture.SharedRedisStore"/>
    /// but has a completely fresh DI container (no local in-memory state).
    /// The caller is responsible for disposing the returned factory after use.
    /// </summary>
    private ApiE2EWebApplicationFactory CreateReplacementReplica()
    {
        var factory = new ApiE2EWebApplicationFactory(
            $"MultiReplica-Failover-{Guid.NewGuid()}",
            Fixture.ConfigStore,
            Fixture.HistoryService,
            new FakeProviderFactory(),
            new FakeKubernetesJobClient(),
            Fixture.ApiKeyValue,
            sharedRedisStore: Fixture.SharedRedisStore);

        // Force Kestrel to bind so DI is fully resolved before we access services.
        using (factory.CreateClient()) { }
        return factory;
    }

    // ── F1: Agent registry survives replica death ──────────────────────────

    [Fact]
    public async Task Failover_AgentRegistry_SurvivesReplica1Death()
    {
        // Arrange: register agents through Replica1
        var agentId1 = $"fo-agent1-{Guid.NewGuid():N}";
        var agentId2 = $"fo-agent2-{Guid.NewGuid():N}";
        RegisterAgent(Fixture.Registry1, agentId1, $"conn-{agentId1}", "dotnet");
        RegisterAgent(Fixture.Registry1, agentId2, $"conn-{agentId2}", "python");

        await Task.Yield();

        // Verify Replica2 sees both before death (baseline)
        Assert.Equal(2, Fixture.Registry2.GetAllAgents().Count(a =>
            a.AgentId.Value == agentId1 || a.AgentId.Value == agentId2));

        // Act: Replica1 "dies" — fresh factory with empty local state, same Redis store
        await using var replica1b = CreateReplacementReplica();
        var registry1b = replica1b.Services.GetRequiredService<IAgentRegistryService>();

        // Assert: replacement replica reads both agents from Redis
        var allAgents = registry1b.GetAllAgents();
        Assert.Contains(allAgents, a => a.AgentId.Value == agentId1);
        Assert.Contains(allAgents, a => a.AgentId.Value == agentId2);

        // GetIdleAgents also works
        var idleAgents = registry1b.GetIdleAgents();
        Assert.Contains(idleAgents, a => a.AgentId.Value == agentId1);
        Assert.Contains(idleAgents, a => a.AgentId.Value == agentId2);
    }

    // ── F2: Agent field updates survive replica death ──────────────────────

    [Fact]
    public async Task Failover_AgentFieldUpdate_SurvivesReplica1Death()
    {
        // Arrange: register agent and update a field through Replica1
        var agentId = $"fo-field-{Guid.NewGuid():N}";
        RegisterAgent(Fixture.Registry1, agentId, $"conn-{agentId}");

        // Fix F2: camelCase field name to match HashToEntry
        var jobId = Guid.NewGuid().ToString("N");
        await Fixture.Registry1.UpdateAgentFieldAsync(new AgentId(agentId), "activeJobId", jobId);

        await Task.Yield();

        // Act: Replica1 dies
        await using var replica1b = CreateReplacementReplica();
        var registry1b = replica1b.Services.GetRequiredService<IAgentRegistryService>();

        // Assert: replacement replica sees the field written before death
        var entry = registry1b.GetByAgentId(new AgentId(agentId));
        Assert.NotNull(entry);
        Assert.Equal(jobId, entry.ActiveJobId);

        // Replica2 (unaffected by the restart) also still sees it
        var entryViaR2 = Fixture.Registry2.GetByAgentId(new AgentId(agentId));
        Assert.NotNull(entryViaR2);
        Assert.Equal(jobId, entryViaR2.ActiveJobId);
    }

    // ── F3: ConnectionId lookup is local — not shared across replicas ──────

    [Fact]
    public async Task Failover_ConnectionIdIndex_IsLocalToReplica_NotShared()
    {
        // This test documents and asserts the intentional design: _connectionIndex is
        // node-local. A connection ID registered on Replica1 is not visible from
        // Replica2 or a replacement replica — only GetByAgentId (Redis-backed) works
        // cross-replica.
        var agentId = $"fo-conn-{Guid.NewGuid():N}";
        var connectionId = $"conn-{agentId}";
        RegisterAgent(Fixture.Registry1, agentId, connectionId);

        await Task.Yield();

        // Replica1 finds the agent by connection ID (local index populated at Register time)
        var viaR1 = Fixture.Registry1.GetByConnectionId(connectionId);
        Assert.NotNull(viaR1);
        Assert.Equal(agentId, viaR1.AgentId.Value);

        // Replica2 does NOT find it by connection ID — correct; it never owned the connection
        var viaR2 = Fixture.Registry2.GetByConnectionId(connectionId);
        Assert.Null(viaR2);

        // A fresh replacement replica also does NOT find it — the local index is cold
        await using var replica1b = CreateReplacementReplica();
        var registry1b = replica1b.Services.GetRequiredService<IAgentRegistryService>();
        var viaR1b = registry1b.GetByConnectionId(connectionId);
        Assert.Null(viaR1b);

        // But GetByAgentId DOES work on all replicas — that path is Redis-backed
        var byAgentId1b = registry1b.GetByAgentId(new AgentId(agentId));
        Assert.NotNull(byAgentId1b);
        Assert.Equal(agentId, byAgentId1b.AgentId.Value);
    }

    // ── F4: Run state survives replica death ───────────────────────────────

    [Fact]
    public async Task Failover_RunState_SurvivesReplica1Death()
    {
        // Arrange: add a run through Replica1
        var runId = Guid.NewGuid().ToString("N");
        Fixture.RunService1.AddRun(new PipelineRun
        {
            RunId = runId,
            IssueIdentifier = "test-org/test-repo#77",
            IssueTitle = "Failover run test",
            IssueProviderConfigId = "issue-e2e",
            RepoProviderConfigId = "repo-e2e",
        });

        await Task.Yield();

        // Act: Replica1 dies
        await using var replica1b = CreateReplacementReplica();
        var runService1b = replica1b.Services.GetRequiredService<IOrchestratorRunService>();

        // Assert: replacement replica reads the run from Redis
        var retrieved = runService1b.GetRun(new RunId(runId));
        Assert.NotNull(retrieved);
        Assert.Equal(runId, retrieved.RunId);
        Assert.Equal("Failover run test", retrieved.IssueTitle);

        // Replica2 (unaffected) also still sees it
        var viaR2 = Fixture.RunService2.GetRun(new RunId(runId));
        Assert.NotNull(viaR2);
        Assert.Equal(runId, viaR2.RunId);
    }

    [Fact]
    public async Task X2_ReRegistration_OnOtherReplica_PreservesActiveJobIdAndBusyStatus()
    {
        // Arrange: register on R1, dispatch (mark Busy + set ActiveJobId)
        var agentId = $"x2-rereg-{Guid.NewGuid():N}";
        RegisterAgent(Fixture.Registry1, agentId, $"conn1-{agentId}", "dotnet");

        var jobId = Guid.NewGuid().ToString("N");
        // camelCase field name to match AgentEntryToHashEntries / HashToEntry
        await Fixture.Registry1.UpdateAgentFieldAsync(new AgentId(agentId), "activeJobId", jobId);
        Fixture.Registry1.TransitionStatus(new AgentId(agentId), AgentStatus.Busy);
        await Task.Delay(50);

        // Act: agent reconnects to Replica2 (new connection ID, same agentId)
        var newConnId = $"conn2-{agentId}";
        var reregistered = Fixture.Registry2.Register(new AgentRegistrationMessage
        {
            AgentId = new AgentId(agentId),
            Hostname = "reconnected-pod",
            Labels = ["dotnet"],
            ActiveJob = null
        }, newConnId);
        await Task.Delay(50); // allow WriteRegistrationAsync fire-and-forget to settle

        // Assert snapshot fields (built before fire-and-forget completes)
        Assert.Equal(AgentStatus.Busy, reregistered.Status);
        Assert.Equal(jobId, reregistered.ActiveJobId);

        // Assert Redis state: read back from the other replica to confirm the hash was written
        var fromRedis = Fixture.Registry1.GetAllAgents().Single(a => a.AgentId.Value == agentId);
        Assert.Equal(AgentStatus.Busy, fromRedis.Status);
        Assert.Equal(jobId, fromRedis.ActiveJobId);

        // Neither replica's idle list should contain this agent
        var idleR1 = Fixture.Registry1.GetIdleAgents();
        var idleR2 = Fixture.Registry2.GetIdleAgents();
        Assert.DoesNotContain(idleR1, a => a.AgentId.Value == agentId);
        Assert.DoesNotContain(idleR2, a => a.AgentId.Value == agentId);
    }

    // ── X3: TransitionStatus visible cross-replica ─────────────────────────

    [Fact]
    public async Task X3_TransitionStatus_ToBusy_ExcludesAgentFromIdleSetOnOtherReplica()
    {
        // Arrange: register on R1
        var agentId = $"x3-transition-{Guid.NewGuid():N}";
        RegisterAgent(Fixture.Registry1, agentId, $"conn-{agentId}");
        await Task.Delay(50); // let WriteRegistrationAsync settle

        // Precondition: visible in both idle lists
        Assert.Contains(Fixture.Registry2.GetIdleAgents(), a => a.AgentId.Value == agentId);

        // Act: transition to Busy on R1 (fires async TransitionStatusAsync)
        Fixture.Registry1.TransitionStatus(new AgentId(agentId), AgentStatus.Busy);
        await Task.Delay(50); // let fire-and-forget settle

        // Assert: Replica2 no longer sees the agent as idle
        var idleAfter = Fixture.Registry2.GetIdleAgents();
        Assert.DoesNotContain(idleAfter, a => a.AgentId.Value == agentId);

        // Agent is Busy in the hash (agents:all entry updated) but NOT in agents:idle
        var allAfter = Fixture.Registry2.GetAllAgents();
        Assert.Contains(allAfter, a => a.AgentId.Value == agentId && a.Status == AgentStatus.Busy);
    }

    // ── X4: Stale idle-set member after hash expiry — GetIdleAgents skips it ─

    [Fact]
    public async Task X4_GetIdleAgents_SkipsStaleSetMember_WhenHashExpired()
    {
        // Arrange: register agent on R1 (writes hash + agents:idle)
        var agentId = $"x4-stale-{Guid.NewGuid():N}";
        RegisterAgent(Fixture.Registry1, agentId, $"conn-{agentId}");
        await Task.Delay(50);

        // Precondition: both replicas see it
        Assert.Contains(Fixture.Registry1.GetIdleAgents(), a => a.AgentId.Value == agentId);
        Assert.Contains(Fixture.Registry2.GetIdleAgents(), a => a.AgentId.Value == agentId);

        // Simulate hash TTL expiry: remove the hash + any expiry entry,
        // but do NOT remove from agents:idle (this is the real Redis scenario —
        // TTL on hash fires, set membership remains until cleanup sweep)
        Fixture.SharedRedisStore.ForceExpire($"agent:{agentId}");
        // Manually remove from agents:all too (mirrors cleanup sweep result)
        // but leave agents:idle populated to reproduce the stale-member scenario
        await Fixture.SharedRedisStore.SetRemoveAsync("agents:all", agentId);

        // Act: GetIdleAgents on R2 — reads members of agents:idle, then HGETALL each
        var idle = Fixture.Registry2.GetIdleAgents();

        // Assert: stale member is silently skipped (hash.Length == 0 → continue)
        Assert.DoesNotContain(idle, a => a.AgentId.Value == agentId);
    }

    // ── X5: Heartbeat on non-owning replica — no ghost entry ─────────────

    [Fact]
    public async Task X5_Heartbeat_OnNonOwningReplica_DoesNotCreateGhostEntry()
    {
        // Arrange: register on R1
        var agentId = $"x5-heartbeat-{Guid.NewGuid():N}";
        RegisterAgent(Fixture.Registry1, agentId, $"conn-{agentId}");
        await Task.Delay(50);

        // Simulate hash TTL expiry (pod evicted, hash gone, sets not yet cleaned)
        Fixture.SharedRedisStore.ForceExpire($"agent:{agentId}");

        // Act: heartbeat fires to R2 (which has no _connectionIndex entry for this agent)
        // UpdateHeartbeat guards: ExistsAsync returns false → logs warning, returns without writing
        Fixture.Registry2.UpdateHeartbeat(new AgentId(agentId), DateTimeOffset.UtcNow);
        await Task.Delay(50);

        // Assert: no ghost hash was created
        Assert.Null(Fixture.Registry1.GetByAgentId(new AgentId(agentId)));
        Assert.Null(Fixture.Registry2.GetByAgentId(new AgentId(agentId)));
    }

    // ── X6: Output backlog cross-replica ──────────────────────────────────

    [Fact]
    public async Task X6_GetOutputBacklog_CrossReplica_ReturnsLinesWrittenByOtherReplica()
    {
        // Arrange: add run and append output lines on R1
        var runId = Guid.NewGuid().ToString("N");
        Fixture.RunService1.AddRun(new PipelineRun
        {
            RunId = runId,
            IssueIdentifier = "test-org/test-repo#101",
            IssueTitle = "Output backlog test",
            IssueProviderConfigId = "issue-e2e",
            RepoProviderConfigId = "repo-e2e",
        });

        var lines = new[] { "line-alpha", "line-beta", "line-gamma" };
        Fixture.RunService1.AppendOutputLines(new RunId(runId), lines);
        await Task.Delay(50); // fire-and-forget RPUSH settles

        // Act: R2 reads the output backlog — this is the SubscribeToRun cross-replica path
        var distributedRunService2 = Fixture.RunService2 as DistributedRunService
            ?? throw new InvalidOperationException(
                $"RunService2 must be DistributedRunService for this test but was {Fixture.RunService2?.GetType().Name}");
        var backlog = await distributedRunService2.GetOutputBacklogAsync(runId);

        // Assert: all lines present cross-replica
        Assert.Equal(lines.Length, backlog.Length);
        Assert.Equal("line-alpha", backlog[0]);
        Assert.Equal("line-beta", backlog[1]);
        Assert.Equal("line-gamma", backlog[2]);
    }

    [Fact]
    public async Task X10_RecentlyCompleted_AfterTtlExpiry_AllowsRedispatch()
    {
        // Arrange: mark completed on R1
        var issueId = new IssueIdentifier($"test-org/test-repo#{Guid.NewGuid():N}");
        var configId = new ProviderConfigId("issue-e2e");

        Fixture.RunService1.MarkRecentlyCompleted(issueId, configId);
        await Task.Yield();

        // Precondition: R2 sees it blocked
        Assert.True(Fixture.RunService2.WasRecentlyCompleted(issueId, configId));

        // Simulate TTL expiry
        var recentlyCompletedKey = $"recently-completed:{configId.Value}:{issueId.Value}";
        Fixture.SharedRedisStore.ForceExpire(recentlyCompletedKey);

        // Assert: after expiry, the issue is eligible for re-dispatch on both replicas
        Assert.False(Fixture.RunService1.WasRecentlyCompleted(issueId, configId));
        Assert.False(Fixture.RunService2.WasRecentlyCompleted(issueId, configId));
    }

    // ── X11: Deregister on non-owning replica removes agent from all sets ─

    [Fact]
    public async Task X11_Deregister_OnNonOwningReplica_RemovesFromAllSets()
    {
        // Arrange: register on R1
        var agentId = $"x11-deregister-{Guid.NewGuid():N}";
        RegisterAgent(Fixture.Registry1, agentId, $"conn-{agentId}");
        await Task.Delay(50);

        // Precondition
        Assert.Contains(Fixture.Registry1.GetAllAgents(), a => a.AgentId.Value == agentId);

        // Act: deregister on R2 (which has no _connectionIndex entry for this agent)
        Fixture.Registry2.Deregister(new AgentId(agentId));
        await Task.Delay(50);

        // Assert: gone from both replicas' views
        Assert.Null(Fixture.Registry1.GetByAgentId(new AgentId(agentId)));
        Assert.Null(Fixture.Registry2.GetByAgentId(new AgentId(agentId)));
        Assert.DoesNotContain(Fixture.Registry1.GetAllAgents(), a => a.AgentId.Value == agentId);
        Assert.DoesNotContain(Fixture.Registry1.GetIdleAgents(), a => a.AgentId.Value == agentId);
    }

    // ── X12: RecentlyCompleted dedup gap — both replicas check before marking

    [Fact]
    public async Task X12_RecentlyCompleted_BothReplicasCheckBeforeMark_DocumentsAdvisoryRace()
    {
        // This test documents that MarkRecentlyCompleted uses plain SET (not SETNX).
        // The guard is advisory — two replicas can both see WasRecentlyCompleted=false
        // before either marks. The real exclusivity guarantee is the Postgres partial
        // unique index on WorkItems. The test asserts the system survives the race
        // (second mark overwrites first, no exception) rather than preventing it.
        var issueId = new IssueIdentifier($"test-org/test-repo#{Guid.NewGuid():N}");
        var configId = new ProviderConfigId("issue-e2e");

        // Both replicas check — both see false (race window)
        Assert.False(Fixture.RunService1.WasRecentlyCompleted(issueId, configId));
        Assert.False(Fixture.RunService2.WasRecentlyCompleted(issueId, configId));

        // Both mark — second overwrites first (plain SET, not SETNX)
        Fixture.RunService1.MarkRecentlyCompleted(issueId, configId);
        Fixture.RunService2.MarkRecentlyCompleted(issueId, configId);
        await Task.Yield();

        // Assert: system survives, key exists (whichever write landed last)
        Assert.True(Fixture.RunService1.WasRecentlyCompleted(issueId, configId));
        Assert.True(Fixture.RunService2.WasRecentlyCompleted(issueId, configId));
    }

    // ── X13: GetActiveRuns with expired hash in active set — skipped gracefully

    [Fact]
    public void X13_GetActiveRuns_ExpiredHashInActiveSet_SkippedGracefully()
    {
        // Arrange: add two runs, then expire one hash
        var runId1 = Guid.NewGuid().ToString("N");
        var runId2 = Guid.NewGuid().ToString("N");

        Fixture.RunService1.AddRun(new PipelineRun
        {
            RunId = runId1,
            IssueIdentifier = "test-org/test-repo#201",
            IssueTitle = "Run 1",
            IssueProviderConfigId = "issue-e2e",
            RepoProviderConfigId = "repo-e2e",
        });
        Fixture.RunService1.AddRun(new PipelineRun
        {
            RunId = runId2,
            IssueIdentifier = "test-org/test-repo#202",
            IssueTitle = "Run 2",
            IssueProviderConfigId = "issue-e2e",
            RepoProviderConfigId = "repo-e2e",
        });

        // Expire run1's hash but leave it in runs:active (simulates TTL firing before cleanup)
        Fixture.SharedRedisStore.ForceExpire($"run:{runId1}");

        // Act: GetActiveRuns on R2 — iterates runs:active, does HGETALL each, skips empty
        var active = Fixture.RunService2.GetActiveRuns();

        // Assert: no exception, stale member skipped, only live run returned
        Assert.DoesNotContain(active, r => r.RunId == runId1);
        Assert.Contains(active, r => r.RunId == runId2);
    }

    // ── X14: ReplaceRun cross-replica — field update visible on other replica

    [Fact]
    public async Task X14_ReplaceRun_CrossReplica_UpdatedFieldVisibleOnOtherReplica()
    {
        // Arrange: add run on R1
        var runId = Guid.NewGuid().ToString("N");
        var run = new PipelineRun
        {
            RunId = runId,
            IssueIdentifier = "test-org/test-repo#301",
            IssueTitle = "ReplaceRun test",
            IssueProviderConfigId = "issue-e2e",
            RepoProviderConfigId = "repo-e2e",
        };
        Fixture.RunService1.AddRun(run);

        // Update via ReplaceRun on R1 (simulates hub method updating step)
        run.CurrentStep = PipelineStep.GeneratingCode;
        Fixture.RunService1.ReplaceRun(run);

        await Task.Yield();

        // Act: R2 reads the run
        var retrieved = Fixture.RunService2.GetRun(new RunId(runId));

        // Assert: updated step is visible cross-replica
        Assert.NotNull(retrieved);
        Assert.Equal(PipelineStep.GeneratingCode, retrieved.CurrentStep);
    }

    // ── Y-series: deeper edge cases from second research round ────────────

    // ── Y1: Heartbeat self-heal does NOT restore agents:idle — documents design gap

    [Fact]
    public async Task Y1_Heartbeat_SelfHeal_RestoresAgentsAll_ButNotAgentsIdle_DesignGap()
    {
        // This test documents a known gap in UpdateHeartbeatAsync: the self-healing path
        // calls SetAddAsync(AgentsAllKey) but NOT SetAddAsync(AgentsIdleKey).
        //
        // Scenario: cleanup sweep removes an Idle agent from agents:all (and agents:idle)
        // while the agent is still alive and sending heartbeats. After the heartbeat fires,
        // the agent is restored to agents:all but remains absent from agents:idle — making
        // it permanently invisible to the dispatcher until it re-registers.
        //
        // Root cause: UpdateHeartbeatAsync line 199 only restores agents:all.
        var agentId = $"y1-selfheal-{Guid.NewGuid():N}";
        RegisterAgent(Fixture.Registry1, agentId, $"conn-{agentId}");
        await Task.Delay(50);

        // Precondition: agent is in both sets
        Assert.Contains(Fixture.Registry1.GetIdleAgents(), a => a.AgentId.Value == agentId);

        // Simulate cleanup sweep removing from both sets (hash remains alive)
        await Fixture.SharedRedisStore.SetRemoveAsync("agents:all", agentId);
        await Fixture.SharedRedisStore.SetRemoveAsync("agents:idle", agentId);

        // Act: heartbeat fires — triggers self-heal
        Fixture.Registry2.UpdateHeartbeat(new AgentId(agentId), DateTimeOffset.UtcNow);
        await Task.Delay(50);

        // Assert: agents:all is restored (current behaviour — self-heal works here)
        Assert.Contains(Fixture.Registry1.GetAllAgents(), a => a.AgentId.Value == agentId);

        // Assert: agents:idle is NOT restored — this is the design gap
        // An Idle agent that survived a cleanup sweep is permanently invisible to SelectAgent
        // until it re-registers. Changing this assert to "Contains" would require
        // UpdateHeartbeatAsync to also call SetAddAsync(AgentsIdleKey) when hash status == Idle.
        Assert.DoesNotContain(Fixture.Registry1.GetIdleAgents(), a => a.AgentId.Value == agentId);
    }

    // ── Y2: Register disabled+busy agent — WriteRegistrationAsync uses SREM not SADD ──

    [Fact]
    public async Task Y2_Register_DisabledAndBusyAgent_DoesNotAddToIdleSet()
    {
        // Arrange: pre-seed hash as disabled + busy (agent crashed mid-run)
        var agentId = $"y2-disabled-busy-{Guid.NewGuid():N}";
        RegisterAgent(Fixture.Registry1, agentId, $"conn1-{agentId}");
        await Task.Delay(50);

        // Mark disabled and give it an active job
        await Fixture.Registry1.UpdateAgentFieldAsync(new AgentId(agentId), "disabled", "True");
        await Fixture.Registry1.UpdateAgentFieldAsync(new AgentId(agentId), "activeJobId", "run-crash");
        Fixture.Registry1.TransitionStatus(new AgentId(agentId), AgentStatus.Busy);
        await Task.Delay(50);

        // Act: agent reconnects to R2 (re-registration path)
        var result = Fixture.Registry2.Register(new AgentRegistrationMessage
        {
            AgentId = new AgentId(agentId),
            Hostname = "reconnected-pod",
            Labels = ["dotnet"],
            ActiveJob = null
        }, $"conn2-{agentId}");
        await Task.Delay(50);

        // Assert: returned snapshot preserves disabled+busy state
        Assert.Equal(AgentStatus.Busy, result.Status);
        Assert.Equal("run-crash", result.ActiveJobId);
        Assert.True(result.Disabled);

        // Assert Redis state: read from the other replica to confirm the hash was written correctly
        // (snapshot assertions above only prove in-memory construction, not the Redis write)
        var fromRedis = Fixture.Registry1.GetAllAgents().Single(a => a.AgentId.Value == agentId);
        Assert.Equal(AgentStatus.Busy, fromRedis.Status);
        Assert.True(fromRedis.Disabled);

        // Critical: WriteRegistrationAsync must have called SREM agents:idle (not SADD)
        Assert.DoesNotContain(Fixture.Registry1.GetIdleAgents(), a => a.AgentId.Value == agentId);
        Assert.DoesNotContain(Fixture.Registry2.GetIdleAgents(), a => a.AgentId.Value == agentId);
    }

    // ── Y3: TransitionStatus idempotency — Idle→Idle cross-replica ────────

    [Fact]
    public async Task Y3_TransitionStatus_IdleToIdle_IsIdempotent_AgentRemainsDispatchable()
    {
        // Two replicas both call TransitionStatus(Idle) on the same agent simultaneously
        // (e.g. reconciliation + normal job-complete racing). Must not corrupt the hash
        // or leave the agent absent from agents:idle.
        var agentId = $"y3-idempotent-{Guid.NewGuid():N}";
        RegisterAgent(Fixture.Registry1, agentId, $"conn-{agentId}");
        await Task.Delay(50);

        // Both replicas call Idle→Idle concurrently
        var t1 = Task.Run(() => Fixture.Registry1.TransitionStatus(new AgentId(agentId), AgentStatus.Idle));
        var t2 = Task.Run(() => Fixture.Registry2.TransitionStatus(new AgentId(agentId), AgentStatus.Idle));
        await Task.WhenAll(t1, t2);
        await Task.Delay(50);

        // Assert: agent still in idle set and hash is internally consistent
        Assert.Contains(Fixture.Registry1.GetIdleAgents(), a => a.AgentId.Value == agentId);
        Assert.Contains(Fixture.Registry2.GetIdleAgents(), a => a.AgentId.Value == agentId);

        var entry = Fixture.Registry2.GetByAgentId(new AgentId(agentId));
        Assert.NotNull(entry);
        Assert.Equal(AgentStatus.Idle, entry.Status);
        Assert.Null(entry.BusySince); // cleared by Idle transition
    }

    [Fact]
    public async Task Y6_AppendOutputLines_CapAt500_OldestEvicted_At501()
    {
        var runId = Guid.NewGuid().ToString("N");
        Fixture.RunService1.AddRun(new PipelineRun
        {
            RunId = runId,
            IssueIdentifier = "test-org/test-repo#501",
            IssueTitle = "Cap test",
            IssueProviderConfigId = "issue-e2e",
            RepoProviderConfigId = "repo-e2e",
        });

        // Push exactly 500 lines via the service (exercises AppendOutputToRedisAsync)
        var initial = Enumerable.Range(0, 500).Select(i => $"line-{i:D4}").ToArray();
        Fixture.RunService1.AppendOutputLines(new RunId(runId), initial);
        await Task.Delay(100); // fire-and-forget settles

        // Verify 500 retained, first line preserved — read via the service API (same as production)
        var distributedRunService1b = Fixture.RunService1 as DistributedRunService
            ?? throw new InvalidOperationException("RunService1 must be DistributedRunService");
        var after500 = await distributedRunService1b.GetOutputBacklogAsync(runId);
        Assert.Equal(500, after500.Length);
        Assert.Equal("line-0000", after500[0]);
        Assert.Equal("line-0499", after500[^1]);

        // Push one more — should evict "line-0000"
        Fixture.RunService1.AppendOutputLines(new RunId(runId), ["line-0500"]);
        await Task.Delay(100);

        // R2 sees the trimmed list (cross-replica validation of the cap)
        var distributedRunService2 = Fixture.RunService2 as DistributedRunService
            ?? throw new InvalidOperationException(
                $"RunService2 must be DistributedRunService for this test but was {Fixture.RunService2?.GetType().Name}");
        var after501 = await distributedRunService2.GetOutputBacklogAsync(runId);
        Assert.Equal(500, after501.Length);
        Assert.DoesNotContain("line-0000", after501);
        Assert.Equal("line-0001", after501[0]);
        Assert.Equal("line-0500", after501[^1]);
    }

    // ── Y7: UpdateRunFieldsAsync targeted HSET — cross-replica visibility ─

    [Fact]
    public async Task Y7_UpdateRunFields_TargetedHSet_VisibleCrossReplica()
    {
        // UpdateRunFieldsAsync does a targeted HSET (not a full ReplaceRun).
        // Used by hub methods like ReportStepTransition to avoid read-modify-write overhead.
        var runId = Guid.NewGuid().ToString("N");
        Fixture.RunService1.AddRun(new PipelineRun
        {
            RunId = runId,
            IssueIdentifier = "test-org/test-repo#702",
            IssueTitle = "UpdateRunFields test",
            IssueProviderConfigId = "issue-e2e",
            RepoProviderConfigId = "repo-e2e",
        });

        var distributedRunService1 = Fixture.RunService1 as DistributedRunService
            ?? throw new InvalidOperationException(
                $"RunService1 must be DistributedRunService for this test but was {Fixture.RunService1?.GetType().Name}");
        await distributedRunService1.UpdateRunFieldsAsync(runId,
            new StackExchange.Redis.HashEntry("currentStep", PipelineStep.GeneratingCode.ToString()),
            new StackExchange.Redis.HashEntry("agentId", "agent-for-test"));

        await Task.Yield();

        // R2 reads — hash was updated in-place, not replaced
        var retrieved = Fixture.RunService2.GetRun(new RunId(runId));
        Assert.NotNull(retrieved);
        Assert.Equal(PipelineStep.GeneratingCode, retrieved.CurrentStep);
        Assert.Equal("agent-for-test", retrieved.AgentId);

        // Other fields from AddRun still intact (partial HSET didn't wipe them)
        Assert.Equal("UpdateRunFields test", retrieved.IssueTitle);
    }

    // ── Y8: HasActiveRuns and ActiveRunCount cross-replica ────────────────

    [Fact]
    public async Task Y8_HasActiveRuns_And_ActiveRunCount_ReflectSharedState_CrossReplica()
    {
        // HasActiveRuns and ActiveRunCount read SetCardinalityAsync(runs:active).
        // They're read on whatever replica handles a dispatch check — must reflect runs
        // created on any replica.
        Assert.False(Fixture.RunService1.HasActiveRuns);
        Assert.Equal(0, Fixture.RunService1.ActiveRunCount);
        Assert.False(Fixture.RunService2.HasActiveRuns);

        // Add runs on R1
        Fixture.RunService1.AddRun(new PipelineRun
        {
            RunId = Guid.NewGuid().ToString("N"),
            IssueIdentifier = "test-org/test-repo#801",
            IssueTitle = "Active count test 1",
            IssueProviderConfigId = "issue-e2e",
            RepoProviderConfigId = "repo-e2e",
        });
        Fixture.RunService1.AddRun(new PipelineRun
        {
            RunId = Guid.NewGuid().ToString("N"),
            IssueIdentifier = "test-org/test-repo#802",
            IssueTitle = "Active count test 2",
            IssueProviderConfigId = "issue-e2e",
            RepoProviderConfigId = "repo-e2e",
        });
        await Task.Yield();

        // R2 sees the same count without having added any runs
        Assert.True(Fixture.RunService2.HasActiveRuns);
        Assert.Equal(2, Fixture.RunService2.ActiveRunCount);

        // R1 agrees
        Assert.Equal(2, Fixture.RunService1.ActiveRunCount);
    }

}
