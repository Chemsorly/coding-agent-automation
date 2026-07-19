using CodingAgentWebUI.Pipeline.Models;

namespace CodingAgentWebUI.Pipeline.Interfaces;

/// <summary>
/// Narrow interface exposing only the dispatch-path operations from
/// <see cref="Services.PipelineOrchestrationService"/>. Consumers that need
/// dedup checks, run creation, and active-run queries for dispatch purposes
/// depend on this interface rather than the full orchestration service.
/// <para>
/// Implementations:
/// <list type="bullet">
///   <item><description><see cref="Services.PipelineOrchestrationService"/> — production implementation</description></item>
/// </list>
/// </para>
/// </summary>
/// <remarks>
/// Extracted to break the concrete coupling between dispatch services
/// (AgentJobDispatcher, DispatchOrchestrationService, PipelineLoopService)
/// and the 14-dependency PipelineOrchestrationService god object.
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
    /// Creates a <see cref="PipelineRun"/> for dispatch to a remote agent.
    /// The run is tracked via <see cref="IOrchestratorRunService"/> (not a local ActiveRun).
    /// Does NOT execute the pipeline locally — the agent handles execution.
    /// </summary>
    /// <returns>The created <see cref="PipelineRun"/> ready for dispatch, or <c>null</c> if the issue is already being processed.</returns>
    Task<PipelineRun?> CreateDispatchedRunAsync(
        ProviderConfigId issueProviderId, ProviderConfigId repoProviderId, IssueIdentifier issueIdentifier,
        ProviderConfigId agentProviderId, string? agentId, CancellationToken ct,
        string? brainProviderId = null, string? pipelineProviderId = null,
        string initiatedBy = "dispatch",
        PipelineRunType runType = PipelineRunType.Implementation);

    /// <summary>
    /// Reserves a run ID and registers a dedup guard (sentinel) without constructing a full
    /// <see cref="PipelineRun"/>. Returns metadata needed to construct the final run.
    /// The sentinel immediately makes <see cref="IsIssueBeingProcessed"/> return <c>true</c>.
    /// </summary>
    /// <returns>A <see cref="RunReservation"/> with the allocated RunId and resolved metadata,
    /// or <c>null</c> if the issue is already being processed.</returns>
    Task<RunReservation?> ReserveRunIdAsync(
        ProviderConfigId issueProviderId, ProviderConfigId repoProviderId, IssueIdentifier issueIdentifier,
        ProviderConfigId agentProviderId, string? agentId, CancellationToken ct,
        string? brainProviderId = null, string? pipelineProviderId = null,
        string initiatedBy = "dispatch");

    /// <summary>
    /// Registers a fully-constructed <see cref="PipelineRun"/> by atomically replacing the
    /// sentinel created by <see cref="ReserveRunIdAsync"/>. The run must use the same RunId
    /// that was returned in the <see cref="RunReservation"/>.
    /// </summary>
    /// <param name="run">The fully-populated pipeline run to register.</param>
    void RegisterDispatchedRun(PipelineRun run);

    /// <summary>
    /// Returns all currently active pipeline runs from both local and multi-run tracking sources.
    /// Used by the loop service for decomposition concurrency enforcement.
    /// </summary>
    IReadOnlyList<PipelineRun> GetAllActiveRuns();
}
