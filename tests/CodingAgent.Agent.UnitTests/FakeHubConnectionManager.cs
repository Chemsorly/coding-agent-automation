using CodingAgent.Pipeline.Models;
using Microsoft.AspNetCore.SignalR.Client;

namespace CodingAgent.Agent.UnitTests;

/// <summary>
/// Test double for <see cref="IHubConnectionManager"/>.
/// Allows scripting StartAsync success/failure and manually firing lifecycle events
/// to exercise <see cref="AgentConnectionLifecycle"/> reconnection logic.
/// Also provides a scriptable <see cref="InvokeAsyncDelegate"/> hook so gate-ordering
/// tests can control whether hub invocations succeed or fail without a real connection.
/// </summary>
internal sealed class FakeHubConnectionManager : IHubConnectionManager
{
    private readonly HubConnection _connection;

    // Controllable behavior
    public Exception? StartException { get; set; }
    public int StartCallCount { get; private set; }
    public int StopCallCount { get; private set; }
    public int DisposeCallCount { get; private set; }

    /// <summary>
    /// Optional delegate invoked instead of the real <see cref="HubConnection.InvokeAsync"/> call
    /// when tests need to control invocation outcome (success, fault, delay) without a live server.
    /// Method name and arguments are passed in; return null to simulate void invocations.
    /// </summary>
    public Func<string, object?[], CancellationToken, Task>? InvokeAsyncDelegate { get; set; }

    /// <summary>
    /// Optional delegate that replaces the default <see cref="StartAsync"/> implementation.
    /// When set, this is called instead of the built-in success/failure logic, allowing tests
    /// to block StartAsync on a <see cref="TaskCompletionSource"/> to exercise gate-ordering
    /// scenarios where the registration window needs to be held open for concurrent assertions.
    /// </summary>
    public Func<CancellationToken, Task>? StartFunc { get; set; }

    // Events (wired by WireEventHandlers)
    // CS0067 suppressed: events are wired via += in WireEventHandlers; the compiler cannot see external subscribers
#pragma warning disable CS0067
    public event Func<string, Task>? OnCancelJob;
    public event Func<ChatPromptMessage, Task>? OnAssignChatPrompt;
    public event Func<string, Task>? OnCancelChat;
    public event Func<FetchModelsRequest, Task>? OnFetchModels;
    public event Func<ConsolidationJobMessage, Task>? OnAssignConsolidationJob;
    public event Func<Task>? OnForceDisconnect;
    public event Func<string?, Task>? OnReconnected;
    public event Func<Exception?, Task>? OnClosed;
#pragma warning restore CS0067

    public HubConnection Connection => _connection;

    /// <summary>Always false — fake connection is never started against a real server.</summary>
    public bool IsConnected => false;

    public FakeHubConnectionManager()
    {
        _connection = new HubConnectionBuilder()
            .WithUrl("http://localhost:9999/hubs/agent")
            .Build();
    }

    public Task StartAsync(CancellationToken ct)
    {
        StartCallCount++;
        ct.ThrowIfCancellationRequested();
        if (StartFunc is not null)
            return StartFunc(ct);
        if (StartException is not null)
            throw StartException;
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken ct)
    {
        StopCallCount++;
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        DisposeCallCount++;
        return ValueTask.CompletedTask;
    }

    // ── Test helpers to fire lifecycle events ────────────────────────────

    public Task SimulateReconnectedAsync(string? connectionId = "new-conn-id")
        => OnReconnected?.Invoke(connectionId) ?? Task.CompletedTask;

    public Task SimulateClosedAsync(Exception? error = null)
        => OnClosed?.Invoke(error) ?? Task.CompletedTask;

    public Task SimulateCancelJobAsync(string jobId)
        => OnCancelJob?.Invoke(jobId) ?? Task.CompletedTask;

    public Task SimulateForceDisconnectAsync()
        => OnForceDisconnect?.Invoke() ?? Task.CompletedTask;

}
