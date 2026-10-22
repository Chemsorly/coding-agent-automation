using CodingAgent.Pipeline.Models;

namespace CodingAgent.Pipeline.Interfaces;

/// <summary>
/// Narrow interface exposing only the dispatch-path operations from the orchestration layer.
/// Consumers that need dedup checks and active-run queries for dispatch purposes
/// depend on this interface rather than the full orchestration service.
/// <para>
/// Implementations:
/// <list type="bullet">
///   <item><description><see cref="Services.DispatchRunCreationService"/> — production implementation</description></item>
/// </list>
/// </para>
/// </summary>
/// <remarks>
/// Extracted to break the concrete coupling between dispatch services
/// (<see cref="Services.DispatchRunCreationService"/>, <see cref="IDispatchOrchestrationService"/>, <see cref="Services.PipelineLoopService"/>)
/// and the orchestration layer.
/// </remarks>
public interface IDispatchRunCreator
{
    /// <summary>
    /// Checks whether the given issue is currently being processed by any active run.
    /// Combines both local (single-run) and multi-run tracking sources.
    /// </summary>
    /// <param name="issueIdentifier">The issue identifier to check.</param>
    /// <param name="issueProviderConfigId">The issue provider config ID.</param>
    /// <returns><c>true</c> if the issue is currently being processed.</returns>
    bool IsIssueBeingProcessed(IssueIdentifier issueIdentifier, ProviderConfigId issueProviderConfigId);

    /// <summary>
    /// Returns all currently active pipeline runs from both local and multi-run tracking sources.
    /// Used by the loop service for decomposition concurrency enforcement.
    /// </summary>
    IReadOnlyList<PipelineRun> GetAllActiveRuns();
}
