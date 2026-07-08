using CodingAgentWebUI.Infrastructure;
using CodingAgentWebUI.Pipeline.Models;
using Microsoft.AspNetCore.SignalR.Client;

namespace CodingAgentWebUI.Agent;

/// <summary>
/// Unified executor interface for all work item task types (implementation, review,
/// decomposition, consolidation). Eliminates task-type branching in <see cref="WorkItemAgentService"/>
/// by routing execution through a single interface regardless of <see cref="WorkItemTaskType"/>.
/// </summary>
public interface IWorkItemExecutor
{
    /// <summary>
    /// Executes the work item and returns a completion payload.
    /// Routing to the appropriate internal executor (pipeline or consolidation) is handled
    /// transparently by the implementation.
    /// </summary>
    /// <param name="assignment">The job assignment containing all task context.</param>
    /// <param name="connection">Hub connection for progress reporting.</param>
    /// <param name="outputBatcher">Output batcher for streaming lines to orchestrator.</param>
    /// <param name="onStepChanged">Callback invoked when the pipeline step changes.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Completion payload with final step, timing, and result metadata.</returns>
    Task<JobCompletionPayload> ExecuteAsync(
        JobAssignmentMessage assignment,
        HubConnection connection,
        OutputBatcher outputBatcher,
        Action<PipelineStep?>? onStepChanged,
        CancellationToken ct);
}
