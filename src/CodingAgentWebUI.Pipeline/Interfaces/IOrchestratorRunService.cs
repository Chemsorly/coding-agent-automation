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
    bool IsIssueBeingProcessed(IssueIdentifier issueIdentifier, ProviderConfigId issueProviderConfigId);

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

    /// <summary>
    /// Appends output lines to the run's persistent storage. For in-memory implementations this
    /// is equivalent to <c>GetOutputBuffer(runId).AddRange(lines)</c>. For distributed
    /// implementations (Redis), this writes to the Redis List for cross-replica backlog serving.
    /// </summary>
    void AppendOutputLines(RunId runId, IReadOnlyList<string> lines);

    /// <summary>Returns the number of currently active runs.</summary>
    int ActiveRunCount { get; }

    /// <summary>
    /// Records that a run for this issue just completed. Used by orphan recovery grace period
    /// to prevent race conditions between run removal and label swap.
    /// </summary>
    void MarkRecentlyCompleted(IssueIdentifier issueIdentifier, ProviderConfigId issueProviderConfigId);

    /// <summary>
    /// Returns <c>true</c> if this issue had a run complete within the last 120 seconds.
    /// Used by <c>OrphanedLabelRecoveryService</c> to avoid incorrectly treating
    /// recently-completed issues as orphaned.
    /// </summary>
    bool WasRecentlyCompleted(IssueIdentifier issueIdentifier, ProviderConfigId issueProviderConfigId);

    /// <summary>
    /// Returns the branch names of all currently active pipeline runs.
    /// Default implementation derives from <see cref="GetActiveRuns"/> — override in
    /// distributed adapters (e.g. <c>SchedulerRunQueryService</c>) that cannot access
    /// in-memory run state directly.
    /// </summary>
    /// <remarks>
    /// Used by <see cref="Services.HousekeepingService"/> Step 4 / Step 6b to guard
    /// against calling <c>UpdatePullRequestBranchAsync</c> on a branch that has an active run.
    /// </remarks>
    // NOTE: The DI registration of SchedulerRunQueryService uses a factory lambda
    //   (sp => new SchedulerRunQueryService(sp.GetRequiredService<IPipelineApiRunHistoryClient>()))
    //   rather than automatic constructor injection. If IPipelineApiRunHistoryClient is not
    //   registered in the Scheduler's service collection, the error is deferred to first use
    //   rather than startup. This is no regression from the previous AddSingleton<SchedulerRunQueryService>()
    //   pattern, but consider adding a startup health check or eager resolution to catch
    //   misconfigured DI at service start rather than at the first housekeeping cycle.
    Task<HashSet<string>> GetActiveRunBranchesAsync(CancellationToken ct = default)
    {
        var branches = GetActiveRuns()
            .Where(r => r.BranchName != null)
            .Select(r => r.BranchName!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return Task.FromResult(branches);
    }
}
