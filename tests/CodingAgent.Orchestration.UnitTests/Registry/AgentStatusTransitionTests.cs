using AwesomeAssertions;
using CodingAgent.Orchestration.Registry;
using CodingAgent.Pipeline.Models;

namespace CodingAgent.Orchestration.UnitTests.Registry;

/// <summary>
/// Direct unit tests for <see cref="AgentStatusTransition.IsAllowed"/>.
/// These tests exercise the extracted state-machine rules in isolation, without going through
/// Redis or <see cref="CodingAgent.Orchestration.Registry.DistributedAgentRegistryService"/>.
/// The end-to-end behavioral test for the rejected edge
/// (<c>TransitionStatus_DisconnectedToBusy_IsRejected_StatusRemainsDisconnected</c>) lives in
/// <c>DistributedAgentRegistryServiceTests.cs</c> and must remain unchanged alongside these tests.
/// </summary>
public sealed class AgentStatusTransitionTests
{
    // ── Rejected edge ─────────────────────────────────────────────────────────

    [Fact]
    public void IsAllowed_DisconnectedToBusy_ReturnsFalse()
    {
        // Disconnected → Busy is the only explicitly rejected edge in the state machine.
        // An agent must re-register (obtaining a fresh connection ID) before it can be
        // dispatched to after disconnecting.
        //
        // TODO: This test verifies the extracted helper in isolation. The wiring inside
        // DistributedAgentRegistryService (the `if (!AgentStatusTransition.IsAllowed(…))` guard
        // that logs the warning and returns early) is covered by the end-to-end test
        // `TransitionStatus_DisconnectedToBusy_IsRejected_StatusRemainsDisconnected` in
        // DistributedAgentRegistryServiceTests.cs. If that test is ever removed or the
        // call-site guard is refactored, add a new integration-level test here to ensure the
        // rejected edge is still acted upon by the service (not just returned as false by the helper).
        AgentStatusTransition.IsAllowed(AgentStatus.Disconnected, AgentStatus.Busy)
            .Should().BeFalse("Disconnected→Busy requires re-registration first");
    }

    // ── Valid transitions (all 8 of 9 ordered pairs that are NOT Disconnected→Busy) ──

    [Theory]
    [InlineData(AgentStatus.Idle,         AgentStatus.Busy)]
    [InlineData(AgentStatus.Busy,         AgentStatus.Idle)]
    [InlineData(AgentStatus.Idle,         AgentStatus.Disconnected)]
    [InlineData(AgentStatus.Busy,         AgentStatus.Disconnected)]
    [InlineData(AgentStatus.Disconnected, AgentStatus.Idle)]
    [InlineData(AgentStatus.Disconnected, AgentStatus.Disconnected)]
    [InlineData(AgentStatus.Idle,         AgentStatus.Idle)]
    [InlineData(AgentStatus.Busy,         AgentStatus.Busy)]
    public void IsAllowed_ValidEdge_ReturnsTrue(AgentStatus from, AgentStatus to)
    {
        // AgentStatus has 3 values → 3×3 = 9 ordered pairs. One is rejected (Disconnected→Busy).
        // The remaining 8 must all be allowed.
        AgentStatusTransition.IsAllowed(from, to)
            .Should().BeTrue($"{from}→{to} is a valid transition");
    }
}
