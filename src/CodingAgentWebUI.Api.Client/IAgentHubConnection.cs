using Microsoft.AspNetCore.SignalR.Client;

namespace CodingAgentWebUI.Api.Client;

/// <summary>
/// Abstraction over a SignalR connection to the agent hub.
/// Used by UI-side consumers (Spec 045) for event subscription only — no invoke wrappers.
/// K8s agents use <c>IAgentConnectionManager</c> / <c>HubConnectionManager</c> from the Agent project.
/// </summary>
public interface IAgentHubConnection : IAsyncDisposable
{
    /// <summary>Current state of the underlying hub connection.</summary>
    HubConnectionState State { get; }

    /// <summary>Start the connection.</summary>
    Task StartAsync(CancellationToken ct = default);

    /// <summary>Register a typed handler for a hub method that receives one argument.</summary>
    IDisposable On<T>(string methodName, Action<T> handler);

    /// <summary>Register a handler for a hub method that receives no arguments.</summary>
    IDisposable On(string methodName, Action handler);
}
