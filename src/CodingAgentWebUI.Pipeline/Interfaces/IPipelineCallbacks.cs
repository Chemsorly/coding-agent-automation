using CodingAgentWebUI.Pipeline.Models;

namespace CodingAgentWebUI.Pipeline.Interfaces;

/// <summary>
/// Pipeline lifecycle callbacks for state transitions, output, and side effects.
/// Implemented by the hosting layer (orchestrator service or agent worker).
/// Consolidates the 7+ callback delegates previously scattered across context objects.
/// </summary>
/// <remarks>
/// <para>
/// <b>Callback Delegation Pattern:</b> Pipeline steps invoke methods on this interface to
/// communicate side effects (state transitions, output, label changes, PR creation) back to
/// the hosting layer without depending on its concrete type. The orchestrator provides the
/// implementation via <c>OrchestratorCallbacks</c> (in <c>PipelineOrchestrationService</c>),
/// while the agent worker provides its own implementation via <c>AgentCallbacks</c>
/// (in <c>LocalPipelineExecutor</c>).
/// </para>
/// <para>
/// This decouples pipeline step logic from the execution environment — the same steps run
/// identically whether orchestrated server-side or executed locally by an agent process.
/// Each implementation delegates to its host's internal services (e.g., lifecycle service,
/// SignalR hub, or issue provider) without exposing those dependencies to the steps.
/// </para>
/// </remarks>
public interface IPipelineCallbacks
{
    /// <summary>Transitions the pipeline run to a new step.</summary>
    void TransitionTo(PipelineStep step);

    /// <summary>Emits an output line for real-time display.</summary>
    void EmitOutputLine(string line);

    /// <summary>Notifies the UI that state has changed (triggers re-render).</summary>
    void NotifyChange();

    /// <summary>Adds a completed run to the history store.</summary>
    void AddRunToHistory(PipelineRun run);

    /// <summary>Updates file change statistics (lines added/removed, file count).</summary>
    Task UpdateFileChangeStats(PipelineRun run);

    /// <summary>Swaps the agent label on the issue (removes all agent labels, adds the new one).</summary>
    Task SwapAgentLabel(string issueIdentifier, string label, CancellationToken ct);

    /// <summary>Removes all agent labels from the issue.</summary>
    Task RemoveAllAgentLabels(string issueIdentifier, CancellationToken ct);

    /// <summary>Creates a pull request for the completed pipeline run.</summary>
    Task CreatePullRequest(PipelineRun run, QualityGateReport report, bool isDraft, CancellationToken ct);
}
