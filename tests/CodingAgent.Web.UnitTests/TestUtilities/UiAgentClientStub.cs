using CodingAgent.Pipeline.Models;
using CodingAgent.Web.Ui;
using Moq;

namespace CodingAgent.Web.UnitTests.TestUtilities;

/// <summary>
/// Spec 051 D8: pages read the agents from <c>/api/ui/agents</c>. An <see cref="IUiAgentClient"/> that
/// returns the given agents (none by default), for components that show agent counts or match labels.
/// </summary>
public static class UiAgentClientStub
{
    public static Mock<IUiAgentClient> With(params AgentEntryDto[] agents)
    {
        var client = new Mock<IUiAgentClient>();
        client.Setup(c => c.GetAgentsAsync(It.IsAny<CancellationToken>())).ReturnsAsync(agents);
        return client;
    }

    /// <summary>An agent with the given labels and status, as the API lists it.</summary>
    public static AgentEntryDto Agent(string id, AgentStatus status = AgentStatus.Idle, bool disabled = false, params string[] labels) => new()
    {
        AgentId = new AgentId(id),
        ConnectionId = $"conn-{id}",
        Hostname = id,
        Labels = labels,
        Status = status,
        Disabled = disabled,
        RegisteredAt = DateTimeOffset.UnixEpoch,
    };
}
