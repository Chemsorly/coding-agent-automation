using CodingAgentWebUI.Pipeline.Models;

namespace CodingAgentWebUI.Pipeline.Interfaces;

/// <summary>
/// Pipeline lifecycle callbacks for state transitions, output, and side effects.
/// Implemented by the hosting layer (orchestrator service or agent worker).
/// Consolidates the 7+ callback delegates previously scattered across context objects.
/// </summary>
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
