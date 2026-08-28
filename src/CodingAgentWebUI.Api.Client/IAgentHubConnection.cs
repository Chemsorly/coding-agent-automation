using Microsoft.AspNetCore.SignalR.Client;

namespace CodingAgentWebUI.Api.Client;

/// <summary>
/// Abstraction over a SignalR connection to the agent hub.
/// Used by UI-side consumers for event subscription only — no invoke wrappers.
/// K8s agents use <c>IAgentConnectionManager</c> / <c>HubConnectionManager</c> from the Agent project.
/// </summary>
public interface IAgentHubConnection : IAsyncDisposable
{
    /// <summary>Current state of the underlying hub connection.</summary>
    HubConnectionState State { get; }

    /// <summary>
    /// Raised when the connection successfully reconnects after a disconnection.
    /// The argument is the new connection ID (may be null if unavailable).
    /// Register handlers here to re-subscribe to hub groups after reconnect.
    /// </summary>
    event Func<string?, Task>? Reconnected;

    /// <summary>Start the connection.</summary>
    Task StartAsync(CancellationToken ct = default);

    /// <summary>Invoke a hub method with no return value.</summary>
    Task InvokeAsync(string methodName, object? arg1, CancellationToken ct = default);

    /// <summary>Register a handler for a hub method that receives no arguments.</summary>
    IDisposable On(string methodName, Action handler);

    /// <summary>Register a typed handler for a hub method that receives one argument.</summary>
    IDisposable On<T>(string methodName, Action<T> handler);

    /// <summary>Register a typed handler for a hub method that receives two arguments.</summary>
    IDisposable On<T1, T2>(string methodName, Action<T1, T2> handler);

    /// <summary>Register a typed handler for a hub method that receives three arguments.</summary>
    IDisposable On<T1, T2, T3>(string methodName, Action<T1, T2, T3> handler);
}
