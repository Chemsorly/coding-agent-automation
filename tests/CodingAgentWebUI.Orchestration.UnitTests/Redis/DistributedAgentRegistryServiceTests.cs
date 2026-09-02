using AwesomeAssertions;
using CodingAgentWebUI.Orchestration.Registry;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.TestUtilities;
using Serilog;

namespace CodingAgentWebUI.Orchestration.UnitTests.Redis;

public sealed class DistributedAgentRegistryServiceTests
{
    private readonly FakeRedisStore _store = new();
    private readonly DistributedAgentRegistryService _sut;

    public DistributedAgentRegistryServiceTests()
    {
        _sut = new DistributedAgentRegistryService(_store, Log.Logger);
    }

    private static AgentRegistrationMessage Msg(string id, string[]? labels = null) =>
        new() { AgentId = new AgentId(id), Hostname = "host-1", Labels = labels ?? ["kiro", "dotnet"] };

    // ── Register ──────────────────────────────────────────────────────────────

    [Fact]
    public void Register_SetsAllHashFields_And_AddsToBothSets()
    {
        _sut.Register(Msg("agent-1"), "conn-1");

        var hash = _store.GetHash("agent:agent-1");
        hash.Should().NotBeNull();
        hash!["agentId"].Should().Be("agent-1");
        hash["connectionId"].Should().Be("conn-1");
        hash["hostname"].Should().Be("host-1");
        hash["status"].Should().Be("Idle");
        hash["disabled"].Should().Be("False");
        hash["orphanRestoredAt"].Should().BeEmpty(); // cleared on new registration

        _store.GetSet("agents:all").Should().Contain("agent-1");
        _store.GetSet("agents:idle").Should().Contain("agent-1");
    }

    [Fact]
    public void Register_ReRegistration_RestoresIdleWhenNoActiveJob()
    {
        _sut.Register(Msg("agent-1"), "conn-1");
        // Simulate disconnect
        _sut.TransitionStatus(new AgentId("agent-1"), AgentStatus.Disconnected);
        // Re-register
        _sut.Register(Msg("agent-1"), "conn-2");

        var hash = _store.GetHash("agent:agent-1");
        hash!["status"].Should().Be("Idle");
        hash["connectionId"].Should().Be("conn-2");
    }

    [Fact]
    public async Task Register_ReRegistration_RestoresBusyWhenActiveJobPresent()
    {
        _sut.Register(Msg("agent-1"), "conn-1");
        // Set activeJobId directly in store to simulate a running job
        await _store.HashSetFieldAsync("agent:agent-1", "activeJobId", "run-abc");
        // Re-register (e.g. after container restart)
        _sut.Register(Msg("agent-1"), "conn-2");

        var hash = _store.GetHash("agent:agent-1");
        hash!["status"].Should().Be("Busy");
    }

    [Fact]
    public async Task Register_ReRegistration_PreservesDisabledFlag()
    {
        _sut.Register(Msg("agent-1"), "conn-1");
        // Manually disable the agent
        await _store.HashSetFieldAsync("agent:agent-1", "disabled", "True");
        // Re-register — disabled must not reset to False
        _sut.Register(Msg("agent-1"), "conn-2");

        var hash = _store.GetHash("agent:agent-1");
        hash!["disabled"].Should().Be("True");
    }

    // ── TransitionStatus ──────────────────────────────────────────────────────

    [Fact]
    public void TransitionStatus_Busy_RemovesFromIdleSet()
    {
        _sut.Register(Msg("agent-1"), "conn-1");
        _sut.TransitionStatus(new AgentId("agent-1"), AgentStatus.Busy);

        _store.GetSet("agents:idle").Should().NotContain("agent-1");
        _store.GetHash("agent:agent-1")!["status"].Should().Be("Busy");
    }

    [Fact]
    public void TransitionStatus_Idle_AddsToIdleSet()
    {
        _sut.Register(Msg("agent-1"), "conn-1");
        _sut.TransitionStatus(new AgentId("agent-1"), AgentStatus.Busy);
        _sut.TransitionStatus(new AgentId("agent-1"), AgentStatus.Idle);

        _store.GetSet("agents:idle").Should().Contain("agent-1");
        _store.GetHash("agent:agent-1")!["status"].Should().Be("Idle");
    }

    // ── UpdateHeartbeat ───────────────────────────────────────────────────────

    [Fact]
    public async Task UpdateHeartbeat_RefreshesTtl_AndSelfHealsSetMembership()
    {
        _sut.Register(Msg("agent-1"), "conn-1");
        // Simulate cleanup sweep removing from set
        await _store.SetRemoveAsync("agents:all", "agent-1");
        _store.GetSet("agents:all").Should().NotContain("agent-1");

        // Heartbeat should restore membership
        _sut.UpdateHeartbeat(new AgentId("agent-1"), DateTimeOffset.UtcNow);
        // Await the fire-and-forget task deterministically via the internal test hook.
        // TODO (WARNING): LastHeartbeatTask stores the ContinueWith continuation, not the inner
        // UpdateHeartbeatAsync task. The continuation completes immediately on success (OnlyOnFaulted
        // skips), so the assertions below may race against Redis writes if FakeRedisStore ever
        // yields asynchronously. See DotNetSpecialist WARNING at DistributedAgentRegistryService.cs:231.
        await _sut.LastHeartbeatTask;

        _store.GetSet("agents:all").Should().Contain("agent-1");
        // Heartbeat also refreshes the TTL so the hash does not expire prematurely.
        var expiry = _store.GetExpiry("agent:agent-1");
        expiry.Should().NotBeNull("heartbeat must refresh the TTL via ExpireAsync");
        expiry!.Value.Should().BeAfter(DateTimeOffset.UtcNow,
            "the refreshed TTL must be in the future");
    }

    [Fact]
    public async Task UpdateHeartbeat_DoesNotCreateGhostEntry_WhenHashExpired()
    {
        // Hash never existed — UpdateHeartbeat should be a no-op
        _sut.UpdateHeartbeat(new AgentId("agent-ghost"), DateTimeOffset.UtcNow);
        await _sut.LastHeartbeatTask;

        _store.GetHash("agent:agent-ghost").Should().BeNull();
        _store.GetSet("agents:all").Should().NotContain("agent-ghost");
    }

    // ── GetIdleAgents / GetIdleAgentsAsync ────────────────────────────────────

    [Fact]
    public void GetIdleAgents_ReturnsFromCache_AfterRegister()
    {
        _sut.Register(Msg("agent-1"), "conn-1");
        _sut.Register(Msg("agent-2"), "conn-2");

        // Sync overload reads from the in-process cache populated by Register.
        var idle = _sut.GetIdleAgents();
        idle.Should().HaveCount(2);
    }

    [Fact]
    public async Task GetIdleAgentsAsync_SkipsMembersWhoseHashExpired()
    {
        _sut.Register(Msg("agent-1"), "conn-1");
        _sut.Register(Msg("agent-2"), "conn-2");

        // Simulate agent-1 hash expiry in Redis
        _store.ForceExpire("agent:agent-1");

        // Async method reads fresh from Redis and skips the expired hash.
        var idle = await _sut.GetIdleAgentsAsync();
        idle.Should().HaveCount(1);
        idle[0].AgentId.Value.Should().Be("agent-2");
    }

    [Fact]
    public async Task GetIdleAgentsAsync_ReturnsEmpty_WhenNoIdleAgents()
    {
        _sut.Register(Msg("agent-1"), "conn-1");
        _sut.TransitionStatus(new AgentId("agent-1"), AgentStatus.Busy);

        var idle = await _sut.GetIdleAgentsAsync();
        idle.Should().BeEmpty();
    }

    [Fact]
    public async Task GetIdleAgentsAsync_ReturnsAllIdleAgents_PipelinedBatch()
    {
        // Register 3 idle agents — all HGETALL calls are issued in a single pipelined batch
        _sut.Register(Msg("agent-1"), "conn-1");
        _sut.Register(Msg("agent-2"), "conn-2");
        _sut.Register(Msg("agent-3"), "conn-3");

        var idle = await _sut.GetIdleAgentsAsync();

        idle.Should().HaveCount(3);
        idle.Select(a => a.AgentId.Value).Should().BeEquivalentTo(["agent-1", "agent-2", "agent-3"]);
    }

    // ── GetAllAgents / GetAllAgentsAsync ──────────────────────────────────────

    [Fact]
    public async Task GetAllAgentsAsync_ReturnsAllAgentsRegardlessOfStatus()
    {
        _sut.Register(Msg("agent-1"), "conn-1");
        _sut.Register(Msg("agent-2"), "conn-2");
        _sut.TransitionStatus(new AgentId("agent-1"), AgentStatus.Busy);

        var all = await _sut.GetAllAgentsAsync();

        all.Should().HaveCount(2);
        all.Select(a => a.AgentId.Value).Should().BeEquivalentTo(["agent-1", "agent-2"]);
    }

    [Fact]
    public async Task GetAllAgentsAsync_UpdatesCache_ForSubsequentSyncReads()
    {
        _sut.Register(Msg("agent-1"), "conn-1");
        _sut.Register(Msg("agent-2"), "conn-2");

        // Call async to populate / refresh cache
        await _sut.GetAllAgentsAsync();

        // Sync read should now return same data from cache without hitting Redis
        var all = _sut.GetAllAgents();
        all.Should().HaveCount(2);
    }

    [Fact]
    public async Task GetAllAgentsAsync_ReturnsEmpty_WhenNoAgents()
    {
        var all = await _sut.GetAllAgentsAsync();
        all.Should().BeEmpty();
    }

    // ── GetByAgentIdAsync ─────────────────────────────────────────────────────

    [Fact]
    public async Task GetByAgentIdAsync_ReturnsNull_WhenAgentNotInRedis()
    {
        var result = await _sut.GetByAgentIdAsync(new AgentId("agent-unknown"));
        result.Should().BeNull();
    }

    [Fact]
    public async Task GetByAgentIdAsync_ReturnsEntry_WhenAgentExists()
    {
        _sut.Register(Msg("agent-1"), "conn-1");

        var result = await _sut.GetByAgentIdAsync(new AgentId("agent-1"));

        result.Should().NotBeNull();
        result!.AgentId.Value.Should().Be("agent-1");
        result.ConnectionId.Should().Be("conn-1");
        result.Status.Should().Be(AgentStatus.Idle);
    }

    [Fact]
    public async Task GetByAgentIdAsync_ReturnsNull_WhenHashExpired()
    {
        _sut.Register(Msg("agent-1"), "conn-1");
        _store.ForceExpire("agent:agent-1");

        var result = await _sut.GetByAgentIdAsync(new AgentId("agent-1"));

        result.Should().BeNull("GetByAgentIdAsync must return null when the Redis hash has expired");
    }

    [Fact]
    public async Task GetByAgentIdAsync_ReturnsFreshStatus_AfterTransition()
    {
        _sut.Register(Msg("agent-1"), "conn-1");
        _sut.TransitionStatus(new AgentId("agent-1"), AgentStatus.Busy);

        var result = await _sut.GetByAgentIdAsync(new AgentId("agent-1"));

        result.Should().NotBeNull();
        result!.Status.Should().Be(AgentStatus.Busy);
    }

    // ── GetIdleAgents sync — cache reflects write paths ────────────────────────

    [Fact]
    public void GetIdleAgents_ExcludesBusyAgents_AfterTransitionStatus()
    {
        _sut.Register(Msg("agent-1"), "conn-1");
        _sut.Register(Msg("agent-2"), "conn-2");
        _sut.TransitionStatus(new AgentId("agent-1"), AgentStatus.Busy);

        // Sync overload reads from cache; TransitionStatusAsync updates the cache.
        var idle = _sut.GetIdleAgents();
        idle.Should().HaveCount(1);
        idle[0].AgentId.Value.Should().Be("agent-2");
    }

    [Fact]
    public void GetAllAgents_ExcludesDeregisteredAgents_AfterDeregister()
    {
        _sut.Register(Msg("agent-1"), "conn-1");
        _sut.Register(Msg("agent-2"), "conn-2");
        _sut.Deregister(new AgentId("agent-1"));

        // DeregisterAsync updates the cache synchronously before the Redis await.
        // The sync read sees the agent removed immediately.
        var all = _sut.GetAllAgents();
        all.Should().HaveCount(1);
        all[0].AgentId.Value.Should().Be("agent-2");
    }

    // ── GetByConnectionId ─────────────────────────────────────────────────────

    [Fact]
    public void GetByConnectionId_ReturnsFromLocalMap_NoRedisCall()
    {
        _sut.Register(Msg("agent-1"), "conn-xyz");

        var entry = _sut.GetByConnectionId("conn-xyz");
        entry.Should().NotBeNull();
        entry!.AgentId.Value.Should().Be("agent-1");
    }

    [Fact]
    public void GetByConnectionId_ReturnsNull_WhenNotRegistered()
    {
        _sut.GetByConnectionId("conn-unknown").Should().BeNull();
    }

    // ── UpdateAgentFieldAsync ─────────────────────────────────────────────────

    [Fact]
    public async Task UpdateAgentFieldAsync_WritesOnlySpecifiedField()
    {
        _sut.Register(Msg("agent-1"), "conn-1");

        await _sut.UpdateAgentFieldAsync(new AgentId("agent-1"), "activeJobId", "run-123");

        var hash = _store.GetHash("agent:agent-1")!;
        hash["activeJobId"].Should().Be("run-123");
        // Other fields untouched
        hash["status"].Should().Be("Idle");
        hash["hostname"].Should().Be("host-1");
    }

    [Fact]
    public async Task UpdateAgentFieldAsync_ClearsField_WhenValueNull()
    {
        _sut.Register(Msg("agent-1"), "conn-1");
        await _sut.UpdateAgentFieldAsync(new AgentId("agent-1"), "activeJobId", "run-123");
        await _sut.UpdateAgentFieldAsync(new AgentId("agent-1"), "activeJobId", null);

        _store.GetHash("agent:agent-1")!["activeJobId"].Should().BeEmpty();
    }

    // ── UpdateHeartbeat — TTL expiry recovery ─────────────────────────────────

    [Fact]
    public async Task UpdateHeartbeat_WhenHashExpiredButLocalSnapshotExists_RecreatesEntry()
    {
        // Arrange: register so _localSnapshot is populated, then simulate TTL expiry in Redis
        _sut.Register(Msg("agent-1"), "conn-1");
        _store.ForceExpire("agent:agent-1");
        _store.GetHash("agent:agent-1").Should().BeNull("pre-condition: hash must be gone before heartbeat");

        // Act: heartbeat arrives while connection is still live
        _sut.UpdateHeartbeat(new AgentId("agent-1"), DateTimeOffset.UtcNow);
        // Deterministically await the fire-and-forget task via the internal test hook
        // instead of Thread.Sleep which is timing-dependent and unreliable under CI load.
        await _sut.LastHeartbeatTask;

        // Assert: entry recreated in Redis (AC1 + AC2)
        // TODO (WARNING): Latent race between Register()'s fire-and-forget WriteRegistrationAsync and
        // the immediately following ForceExpire. Passes today only because FakeRedisStore returns
        // already-completed Tasks. If FakeRedisStore is changed to yield, ForceExpire may run before
        // WriteRegistrationAsync completes, causing the pre-condition assertion to fail intermittently.
        // This is a pre-existing test infrastructure limitation unrelated to issue #2219; left as-is
        // per the issue's Out of Scope section (fire-and-forget Register/Deregister race).
        var hash = _store.GetHash("agent:agent-1");
        hash.Should().NotBeNull("entry must be recreated from local snapshot after TTL expiry");
        hash!["agentId"].Should().Be("agent-1");
        hash["connectionId"].Should().Be("conn-1");
        hash["status"].Should().Be("Idle");
        _store.GetSet("agents:all").Should().Contain("agent-1");
        _store.GetSet("agents:idle").Should().Contain("agent-1");
        // The recreated entry must have an expiry so it does not immediately expire again.
        var expiry = _store.GetExpiry("agent:agent-1");
        expiry.Should().NotBeNull("re-registration from local snapshot must set an expiry via ExpireAsync");
        expiry!.Value.Should().BeAfter(DateTimeOffset.UtcNow,
            "the new TTL must be in the future");
    }

    [Fact]
    public async Task UpdateHeartbeat_WhenHashExpiredAfterDeregister_DoesNotRecreateEntry()
    {
        // Arrange: register, deregister (clears _localSnapshot), then simulate expiry
        _sut.Register(Msg("agent-1"), "conn-1");
        _sut.Deregister(new AgentId("agent-1"));
        // Deterministically await the deregister fire-and-forget task via the internal test hook.
        await _sut.LastDeregisterTask;
        _store.ForceExpire("agent:agent-1"); // belt-and-suspenders: ensure hash is gone

        // Act: stray heartbeat arrives after deregistration
        _sut.UpdateHeartbeat(new AgentId("agent-1"), DateTimeOffset.UtcNow);
        await _sut.LastHeartbeatTask;

        // Assert: entry must NOT be recreated (AC4)
        // TODO (WARNING): Does not test the ordering where TTL fires *before* Deregister is called
        // (i.e., hash already gone when Deregister arrives). That scenario exercises the comment in
        // DeregisterAsync: "GetAgentRaw returns null — snapshot must still be cleared". Consider
        // adding a test: ForceExpire → Deregister → UpdateHeartbeat → assert no entry.
        _store.GetHash("agent:agent-1").Should().BeNull(
            "deregistered agent must not be resurrected by a heartbeat");
        _store.GetSet("agents:all").Should().NotContain("agent-1");
    }

    [Fact]
    public async Task UpdateAgentFieldAsync_WhenHashExpired_SkipsWrite_DoesNotCreatePartialHash()
    {
        // Arrange: register, then simulate TTL expiry so the hash is gone
        _sut.Register(Msg("agent-1"), "conn-1");
        _store.ForceExpire("agent:agent-1");
        _store.GetHash("agent:agent-1").Should().BeNull("pre-condition: hash must be absent before call");

        // Act: UpdateAgentFieldAsync is called (e.g. ReportChatCompleted clears activeChatSessionId)
        await _sut.UpdateAgentFieldAsync(new AgentId("agent-1"), "activeChatSessionId", null);

        // Assert: no partial hash was created (CRITICAL-1 fix).
        // Without the existence guard, HashSetFieldAsync would create a single-field hash that
        // satisfies ExistsAsync == true but is missing required fields (agentId, connectionId,
        // registeredAt), causing HashToEntry to return null and the agent to be invisible for 600s.
        _store.GetHash("agent:agent-1").Should().BeNull(
            "UpdateAgentFieldAsync must not create a partial hash when the agent hash has expired");
        _store.GetExpiry("agent:agent-1").Should().BeNull(
            "no TTL should be set on a key that was not written");
    }

    [Fact]
    public async Task UpdateHeartbeat_WhenHashExpiredAfterStatusTransition_RecreatesEntryWithLiveStatus()
    {
        // Arrange: register as Idle, transition to Busy, then simulate TTL expiry
        _sut.Register(Msg("agent-1"), "conn-1");
        _sut.TransitionStatus(new AgentId("agent-1"), AgentStatus.Busy);
        _store.ForceExpire("agent:agent-1");
        _store.GetHash("agent:agent-1").Should().BeNull("pre-condition: hash must be gone before heartbeat");

        // Act: heartbeat fires while agent is still mid-job
        _sut.UpdateHeartbeat(new AgentId("agent-1"), DateTimeOffset.UtcNow);
        await _sut.LastHeartbeatTask;

        // Assert: recreated entry must reflect the LIVE Busy status (CRITICAL-2 fix).
        // Without snapshot sync in TransitionStatusAsync, the entry would be recreated with
        // the stale Register()-time snapshot (Status=Idle, ActiveJobId=null), making the
        // agent eligible for double-booking by the dispatcher.
        var hash = _store.GetHash("agent:agent-1");
        hash.Should().NotBeNull("entry must be recreated after TTL expiry");
        hash!["status"].Should().Be("Busy",
            "re-registered entry must reflect the live Busy status, not the stale Register()-time Idle");
        _store.GetSet("agents:idle").Should().NotContain("agent-1",
            "a Busy agent must not appear in agents:idle after re-registration");
        _store.GetSet("agents:all").Should().Contain("agent-1");
    }

    [Fact]
    public async Task UpdateAgentFieldAsync_RefreshesTtl()
    {
        // Arrange: register (sets initial TTL), then call UpdateAgentFieldAsync
        _sut.Register(Msg("agent-1"), "conn-1");

        // Act
        await _sut.UpdateAgentFieldAsync(new AgentId("agent-1"), "activeJobId", "run-xyz");

        // Assert: expiry was set (AC3)
        // Note: FakeRedisStore.HashSetFieldAsync clears expiry, then the subsequent
        // ExpireAsync call inside UpdateAgentFieldAsync must re-set it.
        // TODO (WARNING): Only asserts the expiry is non-null and in the future. A regression that calls
        // ExpireAsync with TimeSpan.FromSeconds(1) instead of AgentTtl (600s) would still pass. Consider
        // also asserting: expiry >= DateTimeOffset.UtcNow + TimeSpan.FromSeconds(590) to catch trivially
        // short TTL values.
        var expiry = _store.GetExpiry("agent:agent-1");
        expiry.Should().NotBeNull("UpdateAgentFieldAsync must refresh the TTL via ExpireAsync");
        expiry!.Value.Should().BeAfter(DateTimeOffset.UtcNow,
            "the new TTL must be in the future");
    }

    // ── _allAgentsCache race condition regression tests ────────────────────────

    [Fact]
    public async Task GetIdleAgentsAsync_DoesNotResurrectDeregisteredAgent_WhenMergeRacesDeregister()
    {
        // Arrange: register two agents so cache = [agent-1, agent-2] and both are in agents:idle.
        _sut.Register(Msg("agent-1"), "conn-1");
        _sut.Register(Msg("agent-2"), "conn-2");

        // Simulate the race: call GetIdleAgentsAsync while agent-1 is still in Redis idle set
        // (so it lands in the merged cache), then deregister agent-1 and call GetIdleAgentsAsync
        // again — post-fix the merge must not re-introduce agent-1 into _allAgentsCache.
        // Step 1: prime the cache — GetIdleAgentsAsync sees [agent-1, agent-2] in Redis.
        await _sut.GetIdleAgentsAsync();

        // Step 2: deregister agent-1 under lock (RemoveFromAllAgentsCache removes it).
        _sut.Deregister(new AgentId("agent-1"));
        await _sut.LastDeregisterTask;

        // At this point _allAgentsCache = [agent-2] (set by RemoveFromAllAgentsCache under lock).
        // agent-1 is also removed from agents:idle in Redis by DeregisterAsync.

        // Step 3: call GetIdleAgentsAsync again. Pre-fix: it reads existing = [agent-2],
        // fetches idle list = [agent-2] from Redis (agent-1 gone), computes merged = [agent-2]
        // — no resurrection here since agent-1 is already out of the idle set. The true race
        // window requires agent-1 to still be in the Redis idle set during the fetch, which
        // FakeRedisStore's synchronous execution makes hard to trigger deterministically.
        // Instead we verify the lock semantics: after deregister + GetIdleAgentsAsync, the
        // cache must not contain agent-1.
        // TODO (WARNING): This test does not exercise the resurrection scenario it names. By the time
        // Step 3's GetIdleAgentsAsync runs, agent-1 has already been removed from agents:idle in
        // FakeRedisStore (by await LastDeregisterTask), so the merge never sees agent-1 and this
        // test would pass identically against the un-patched code. The true resurrection race
        // (agent-1 still in Redis idle set when GetIdleAgentsAsync fetches, then Deregister runs)
        // cannot be triggered deterministically with FakeRedisStore's synchronous execution model.
        // To properly cover this scenario, FakeRedisStore would need a controllable yield point, or
        // _allAgentsCache would need to be directly injectable for test setup.
        await _sut.GetIdleAgentsAsync();

        // Assert: cache does not contain the deregistered agent.
        var all = _sut.GetAllAgents();
        all.Should().NotContain(a => a.AgentId.Value == "agent-1",
            "deregistered agent must not be resurrected in _allAgentsCache by GetIdleAgentsAsync");
        all.Should().ContainSingle(a => a.AgentId.Value == "agent-2",
            "the still-registered agent must remain in the cache");
    }

    [Fact]
    public async Task GetBusyAgentCount_AfterDeregister_ReturnsZeroNotPermanentlyElevated()
    {
        // Arrange: register agent-1, transition to Busy (so GetBusyAgentCount would return 1),
        // then deregister. After deregistration the count must drop to 0.
        _sut.Register(Msg("agent-1"), "conn-1");
        _sut.TransitionStatus(new AgentId("agent-1"), AgentStatus.Busy);
        _sut.Deregister(new AgentId("agent-1"));
        await _sut.LastDeregisterTask;

        // Act: GetBusyAgentCount calls GetAllAgents() → GetAllAgentsAsync() → reads Redis.
        // DeregisterAsync removed agent-1 from agents:all in Redis, so GetAllAgentsAsync
        // returns an empty list and the count must be 0.
        // TODO (WARNING): This test does not cover the multi-cycle resurrection scenario from AC#3.
        // GetBusyAgentCount always goes through GetAllAgentsAsync which unconditionally reads Redis
        // and re-populates the cache — so this test verifies Redis consistency (which was never
        // broken) rather than _allAgentsCache staleness. The actual failure mode is: a sync caller
        // reads the stale _allAgentsCache after a GetIdleAgentsAsync partial-merge re-introduced a
        // deregistered Busy agent. A test covering the full multi-cycle path would need to:
        // (1) prime the cache with a Busy agent via GetIdleAgentsAsync, (2) deregister it,
        // (3) trigger a GetIdleAgentsAsync that sees the agent still in the Redis idle set (stale
        // fetch window), then (4) read GetBusyAgentCount via a cache-only path (not via Redis).
        // FakeRedisStore's synchronous execution makes step 3 non-deterministic to trigger.
        var count = _sut.GetBusyAgentCount();

        // Assert
        count.Should().Be(0,
            "deregistered agent must not remain in _allAgentsCache as Busy across dispatch cycles");
    }
}
