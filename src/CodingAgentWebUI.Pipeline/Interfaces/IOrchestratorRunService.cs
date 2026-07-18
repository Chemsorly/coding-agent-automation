using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Pipeline.Services;

namespace CodingAgentWebUI.Pipeline.Interfaces;

/// <summary>
/// Abstraction for multi-run tracking. Allows <see cref="PipelineOrchestrationService"/>
/// to check for concurrent agent runs without depending on the WebUI project.
/// </summary>
public interface IOrchestratorRunService
{
    /// <summary>Returns <c>true</c> if any pipeline runs are currently active.</summary>
    bool HasActiveRuns { get; }

    /// <summary>Checks whether the given issue identifier is being processed by any active run.</summary>
    bool IsIssueBeingProcessed(string issueIdentifier, string issueProviderConfigId);

    /// <summary>Returns all active runs as a read-only snapshot.</summary>
    IReadOnlyList<PipelineRun> GetActiveRuns();

    /// <summary>Gets a specific run by its <see cref="PipelineRun.RunId"/>.</summary>
    PipelineRun? GetRun(RunId runId);

    /// <summary>Adds a pipeline run to the active runs collection.</summary>
    void AddRun(PipelineRun run);

    /// <summary>Removes a pipeline run from the active runs collection.</summary>
    PipelineRun? RemoveRun(RunId runId);

    /// <summary>
    /// Atomically replaces an existing run with a new instance (same RunId).
    /// Used by dispatch to update a run with additional metadata without
    /// creating a gap where IsIssueBeingProcessed returns false.
    /// </summary>
    void ReplaceRun(PipelineRun run);

    /// <summary>Gets or creates the per-run <see cref="OutputRingBuffer"/> for the specified run.</summary>
    OutputRingBuffer GetOutputBuffer(RunId runId);

    /// <summary>Returns the number of currently active runs.</summary>
    int ActiveRunCount { get; }
}
