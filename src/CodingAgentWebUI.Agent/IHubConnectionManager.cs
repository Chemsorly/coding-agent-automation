using CodingAgentWebUI.Pipeline.Models;
using Microsoft.AspNetCore.SignalR.Client;

namespace CodingAgentWebUI.Agent;

/// <summary>
/// Abstraction over <see cref="HubConnectionManager"/> that enables unit testing of
/// <see cref="AgentConnectionLifecycle"/> reconnection logic without real SignalR connections.
/// </summary>
public interface IHubConnectionManager : IAsyncDisposable
{
    /// <summary>The underlying SignalR hub connection for invoking server methods.</summary>
    HubConnection Connection { get; }

    /// <summary>Whether the connection is currently active.</summary>
    bool IsConnected { get; }

    Task StartAsync(CancellationToken ct);
    Task StopAsync(CancellationToken ct);

    // Business events — wired by AgentConnectionLifecycle.WireEventHandlers
    event Func<JobAssignmentMessage, Task>? OnAssignJob;
    event Func<string, Task>? OnCancelJob;
    event Func<ChatPromptMessage, Task>? OnAssignChatPrompt;
    event Func<string, Task>? OnCancelChat;
    event Func<FetchModelsRequest, Task>? OnFetchModels;
    event Func<ConsolidationJobMessage, Task>? OnAssignConsolidationJob;
    event Func<Task>? OnForceDisconnect;
    event Func<string?, Task>? OnReconnected;
    event Func<Exception?, Task>? OnClosed;
}
