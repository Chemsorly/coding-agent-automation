using CodingAgentWebUI.Pipeline.Models;
using Microsoft.AspNetCore.SignalR.Client;

namespace CodingAgentWebUI.Agent;

/// <summary>
/// Shared connection lifecycle manager used by both <see cref="AgentWorkerService"/> (long-running)
/// and <see cref="WorkItemAgentService"/> (K8s ephemeral). Encapsulates:
/// <list type="bullet">
///   <item>SignalR connection + automatic reconnection with re-registration</item>
///   <item>Polly resilience pipeline for hub invocations</item>
///   <item>Periodic heartbeat loop</item>
///   <item>CancelJob event forwarding</item>
///   <item>Graceful deregistration on dispose</item>
/// </list>
/// </summary>
public interface IAgentConnectionManager : IAsyncDisposable
{
    /// <summary>
    /// Connects to the orchestrator hub, registers the agent, and starts the heartbeat loop.
    /// </summary>
    /// <param name="registration">The registration message to send (includes labels, active job, etc.).</param>
    /// <param name="ct">Cancellation token for the connection lifetime.</param>
    Task ConnectAndRegisterAsync(AgentRegistrationMessage registration, CancellationToken ct);

    /// <summary>
    /// Invokes a hub method with Polly resilience (retry + timeout).
    /// Use this for all critical hub calls instead of bare <c>Connection.InvokeAsync</c>.
    /// </summary>
    Task InvokeAsync(Func<HubConnection, CancellationToken, Task> action, CancellationToken ct);

    /// <summary>
    /// Invokes a hub method with Polly resilience and returns a result.
    /// </summary>
    Task<T> InvokeAsync<T>(Func<HubConnection, CancellationToken, Task<T>> action, CancellationToken ct);

    /// <summary>
    /// The underlying hub connection for cases where direct access is needed
    /// (e.g., passing to <see cref="LocalPipelineExecutor"/>).
    /// Prefer <see cref="InvokeAsync"/> for resilient calls.
    /// </summary>
    HubConnection Connection { get; }

    /// <summary>
    /// Whether the hub connection is currently active.
    /// </summary>
    bool IsConnected { get; }

    /// <summary>
    /// Fired when the orchestrator sends a CancelJob message for the given job ID.
    /// Services should wire this to cancel their pipeline CTS.
    /// </summary>
    event Func<string, Task>? OnCancelJobReceived;

    /// <summary>
    /// Fired when the orchestrator requests a forced disconnection (e.g., during rolling upgrade).
    /// Services should cancel running work and prepare for shutdown.
    /// </summary>
    event Func<Task>? OnForceDisconnect;

    /// <summary>
    /// Fired when the connection is re-established after a disconnection.
    /// The manager automatically re-registers; this event is informational.
    /// </summary>
    event Func<Task>? OnReconnected;

    /// <summary>
    /// Updates the current pipeline step reported in heartbeats.
    /// </summary>
    void UpdateCurrentStep(PipelineStep? step);

    /// <summary>
    /// Updates the registration message used for re-registration after reconnection.
    /// Call this when the agent's state changes (e.g., starts a new job).
    /// </summary>
    void UpdateRegistration(AgentRegistrationMessage registration);
}
