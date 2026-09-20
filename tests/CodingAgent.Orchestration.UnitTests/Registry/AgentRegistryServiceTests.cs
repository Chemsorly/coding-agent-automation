using AwesomeAssertions;
using CodingAgent.Orchestration.Registry;
using CodingAgent.Pipeline.Models;
using Moq;
using ILogger = Serilog.ILogger;

namespace CodingAgent.Orchestration.UnitTests.Registry;

/// <summary>
/// Unit tests for <see cref="AgentRegistryService"/> (in-memory fallback).
/// Focused on the <c>preserveExistingConnectionId</c> parameter added in issue #2758.
/// </summary>
public sealed class AgentRegistryServiceTests
{
    private static AgentRegistryService CreateRegistry() =>
        new(Mock.Of<ILogger>());

    private static AgentRegistrationMessage Msg(string id) =>
        new() { AgentId = new AgentId(id), Hostname = "host-1", Labels = ["kiro", "dotnet"] };

    // ── preserveExistingConnectionId ─────────────────────────────────────────

    [Fact]
    public void Register_WithPreserveExistingConnectionId_KeepsBothConnections()
    {
        var registry = CreateRegistry();

        // Arrange: register on conn-A and simulate an in-flight job
        var entryA = registry.Register(Msg("agent-1"), "conn-A");
        // TODO (WARNING, issue #2758): Directly mutating the returned AgentEntry is a white-box
        // reliance on AgentRegistryService's reference-sharing behaviour (_agents and _connectionIndex
        // hold the same object). If Register ever returns a defensive copy (as DistributedAgentRegistryService
        // already does), this mutation will silently stop setting ActiveJobId on the stored entry and
        // the test will continue to pass without covering the intended scenario. Also, the test does not
        // verify reference identity of the conn-A lookup before and after the second Register, meaning
        // a broken implementation that evicts and re-inserts conn-A pointing to the same entry would
        // also pass. Consider using TransitionStatus/AssignJob APIs (if available) to set ActiveJobId,
        // and add a pre/post reference-identity assertion to lock in the eviction-guard behaviour.
        entryA.ActiveJobId = "job-123";

        // Act: mid-run kiro-cli sub-process reconnects on conn-B without an ActiveJob in the message
        registry.Register(Msg("agent-1"), "conn-B", preserveExistingConnectionId: true);

        // Assert: both connections are resolvable — conn-A must not be evicted
        registry.GetByConnectionId("conn-A").Should().NotBeNull(
            "conn-A must remain in _connectionIndex so hub calls on the active pipeline connection " +
            "continue to pass AgentAuthorizationFilter (issue #2758)");

        registry.GetByConnectionId("conn-B").Should().NotBeNull(
            "conn-B must be registered as the new primary connection");
    }

    [Fact]
    public void Register_WithPreserveExistingConnectionId_ActiveJobIdRetainedOnBothLookups()
    {
        var registry = CreateRegistry();

        // Arrange: register on conn-A with an active job
        var entryA = registry.Register(Msg("agent-1"), "conn-A");
        entryA.ActiveJobId = "job-xyz";

        // Act
        registry.Register(Msg("agent-1"), "conn-B", preserveExistingConnectionId: true);

        // Assert: the entry returned for conn-A has ActiveJobId intact
        // (AgentAuthorizationFilter's [RequiresActiveJob] check depends on this)
        var resolvedViaConnA = registry.GetByConnectionId("conn-A");
        resolvedViaConnA.Should().NotBeNull();
        resolvedViaConnA!.ActiveJobId.Should().Be("job-xyz",
            "ActiveJobId must be preserved so [RequiresActiveJob] hub method checks pass on conn-A");
    }

    [Fact]
    public void Register_WithoutPreserveExistingConnectionId_EvictsOldConnection()
    {
        var registry = CreateRegistry();

        // Arrange: normal first registration on conn-A
        registry.Register(Msg("agent-1"), "conn-A");

        // Act: normal re-registration on conn-B (default preserveExistingConnectionId=false)
        registry.Register(Msg("agent-1"), "conn-B");

        // Assert: conn-A is evicted — normal re-registration path must be unchanged
        registry.GetByConnectionId("conn-A").Should().BeNull(
            "the normal re-registration path must evict the old connection from _connectionIndex");
        registry.GetByConnectionId("conn-B").Should().NotBeNull(
            "conn-B must be registered as the new connection");
    }
}
