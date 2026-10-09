using CodingAgent.Pipeline.Models;

namespace CodingAgent.Pipeline.Interfaces;

/// <summary>
/// Manages the set of currently active pipeline runs.
/// </summary>
/// <remarks>
/// <para>
/// <b>Duplicate <see cref="AddRun"/>:</b> When a run with the same <see cref="PipelineRun.RunId"/>
/// already exists, the second call replaces the stored run and the run remains active with its
/// backlog preserved. This matches the Redis path (which overwrites the hash) and satisfies
/// <c>AgentOrphanRecoveryService</c>'s re-activation pattern.
/// </para>
/// <para>
/// <b><see cref="ReplaceRun"/> on an inactive RunId:</b> If the RunId is not currently active
/// (never added, or already removed), the call logs a warning and changes nothing. It does NOT
/// resurrect removed runs.
/// </para>
/// <para>
/// <b><see cref="GetRun"/> snapshot semantics:</b> Returns the live in-memory object for in-memory
/// implementations, or a deserialized copy for Redis implementations. Changes are only visible to
/// later <see cref="GetRun"/> and <see cref="RemoveRun"/> calls after a <see cref="ReplaceRun"/>.
/// </para>
/// </remarks>
public interface IActiveRunRegistry
{
    /// <summary>Returns <c>true</c> if any pipeline runs are currently active.</summary>
    bool HasActiveRuns { get; }

    /// <summary>Returns the number of currently active runs.</summary>
    int ActiveRunCount { get; }

    /// <summary>Returns all active runs as a read-only snapshot.</summary>
    IReadOnlyList<PipelineRun> GetActiveRuns();

    /// <summary>Gets a specific run by its <see cref="PipelineRun.RunId"/>.</summary>
    PipelineRun? GetRun(RunId runId);

    /// <summary>
    /// Adds a pipeline run to the active runs collection.
    /// If a run with the same <see cref="PipelineRun.RunId"/> already exists, the stored run
    /// is replaced (upsert) and the existing backlog is preserved.
    /// </summary>
    void AddRun(PipelineRun run);

    /// <summary>Removes a pipeline run from the active runs collection.</summary>
    PipelineRun? RemoveRun(RunId runId);

    /// <summary>
    /// Atomically replaces an existing run with a new instance (same RunId).
    /// If the RunId is not currently active, logs a warning and does nothing.
    /// </summary>
    void ReplaceRun(PipelineRun run);
}
