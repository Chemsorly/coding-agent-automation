using CodingAgentWebUI.Hub;
using Microsoft.AspNetCore.SignalR;
using Moq;

namespace CodingAgentWebUI.UnitTests.Hubs;

/// <summary>
/// Shared helpers for AgentHub unit tests.
/// </summary>
internal static class HubTestHelpers
{
    /// <summary>
    /// Creates a no-op <see cref="IHubContext{AgentHub}"/> mock suitable for unit tests.
    /// The mock returns a non-null <see cref="IHubClients"/> with a <c>Group</c> call that
    /// returns a client proxy whose <c>SendAsync</c> returns a completed task.
    /// Required because AgentHub.Lifecycle.cs calls <c>_uiContext.Clients.Group(...).SendAsync(...)</c>
    /// after the hub group push was added in Spec 044 Task 5.
    /// </summary>
    public static IHubContext<AgentHub> CreateNoOpHubContext()
    {
        var mockClientProxy = new Mock<IClientProxy>();
        mockClientProxy
            .Setup(p => p.SendCoreAsync(It.IsAny<string>(), It.IsAny<object[]>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var mockClients = new Mock<IHubClients>();
        mockClients
            .Setup(c => c.Group(It.IsAny<string>()))
            .Returns(mockClientProxy.Object);

        var mockContext = new Mock<IHubContext<AgentHub>>();
        mockContext
            .Setup(h => h.Clients)
            .Returns(mockClients.Object);

        return mockContext.Object;
    }
}
