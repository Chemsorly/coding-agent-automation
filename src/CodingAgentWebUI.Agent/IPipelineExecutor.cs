using CodingAgentWebUI.Infrastructure;
using CodingAgentWebUI.Pipeline.Models;
using Microsoft.AspNetCore.SignalR.Client;

namespace CodingAgentWebUI.Agent;

/// <summary>
/// Executes the full pipeline for job assignments (implementation, review, decomposition).
/// Both <see cref="WorkItemExecutorRouter"/> (K8s mode) and <see cref="AgentWorkerService"/> (SignalR mode)
/// consume this interface, ensuring pipeline execution behavior is consistent across modes.
/// </summary>
/// <remarks>
/// <para>Implemented by <see cref="LocalPipelineExecutor"/> which resolves providers,
/// builds the step pipeline, executes each step sequentially, and reports progress
/// back to the orchestrator via the hub connection.</para>
/// </remarks>
public interface IPipelineExecutor
{
    /// <summary>
    /// Executes the full pipeline for the given job assignment.
    /// Reports all progress to the orchestrator via the hub connection.
    /// </summary>
    /// <param name="job">The job assignment message containing issue context and configuration.</param>
    /// <param name="connection">The SignalR hub connection for progress reporting.</param>
    /// <param name="outputBatcher">Batcher for streaming output lines to the orchestrator.</param>
    /// <param name="onStepChanged">Callback invoked when the current pipeline step changes.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Completion payload with final step, timing, and result metadata.</returns>
    Task<JobCompletionPayload> ExecuteAsync(
        JobAssignmentMessage job,
        HubConnection connection,
        OutputBatcher outputBatcher,
        Action<PipelineStep?>? onStepChanged,
        CancellationToken ct);
}
