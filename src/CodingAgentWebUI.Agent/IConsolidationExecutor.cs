using CodingAgentWebUI.Pipeline.Models;
using Microsoft.AspNetCore.SignalR.Client;

namespace CodingAgentWebUI.Agent;

/// <summary>
/// Executes consolidation jobs (brain consolidation, refactoring detection, harness suggestions).
/// Both <see cref="WorkItemExecutorRouter"/> (K8s mode) and <see cref="AgentWorkerService"/> (SignalR mode)
/// consume this interface, ensuring consolidation behavior is consistent across execution modes.
/// </summary>
/// <remarks>
/// <para>Implemented by <see cref="LocalConsolidationExecutor"/> which resolves provider instances,
/// dispatches to type-specific executors, and reports results via the hub connection.</para>
/// </remarks>
public interface IConsolidationExecutor
{
    /// <summary>
    /// Executes a consolidation job and reports the result back to the orchestrator.
    /// </summary>
    /// <param name="job">The consolidation job message from the orchestrator.</param>
    /// <param name="connection">The SignalR hub connection for reporting results.</param>
    /// <param name="ct">Cancellation token (linked to shutdown and agent timeout).</param>
    /// <returns>The consolidation job result.</returns>
    Task<ConsolidationJobResult> ExecuteAsync(
        ConsolidationJobMessage job,
        HubConnection connection,
        CancellationToken ct);
}
