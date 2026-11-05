using System.Reflection;
using AwesomeAssertions;
using CodingAgent.Api.Dispatch;
using CodingAgent.Kubernetes;
using CodingAgent.Orchestration.Dispatch;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CodingAgent.Api.IntegrationTests.AgentCapacity;

/// <summary>
/// Spec 057 req 3.3, D8, DD4: work-item dispatch and chat launch share one agent start gate, so the one lock serializes
/// every agent start (the race itself is covered by <c>AgentStartGateTests.ConcurrentStarts_ForTheLastSlot_CreateExactlyOneJob</c>).
/// </summary>
[Collection(ApiIntegrationTestCollection.Name)]
public sealed class AgentStartWiringTests(ApiWebApplicationFactory factory)
{
    private static object? GateOf(object owner) =>
        owner.GetType().GetField("_gate", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(owner);

    [Fact]
    public void DispatchAndChat_UseTheSameRealGate()
    {
        var gate = factory.Services.GetRequiredService<IAgentStartGate>();
        var dispatch = factory.Services.GetRequiredService<DispatchWorkItemService>();
        var chat = factory.Services.GetRequiredService<ChatJobDispatcher>();

        gate.Should().BeOfType<AgentStartGate>("production wiring must use the real gate, never a pass-through");
        GateOf(dispatch).Should().BeSameAs(gate);
        GateOf(chat).Should().BeSameAs(gate);
    }
}
