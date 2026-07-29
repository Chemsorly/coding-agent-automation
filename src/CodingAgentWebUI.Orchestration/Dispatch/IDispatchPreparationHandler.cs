using CodingAgentWebUI.Pipeline.Models;

namespace CodingAgentWebUI.Orchestration.Dispatch;

/// <summary>
/// Strategy interface for dispatch preparation. Each implementation handles a specific
/// dispatch type (Implementation, Review, Decomposition) by performing the variant-specific
/// middle portion of the Template Method pattern in <see cref="AgentJobDispatcher"/>.
/// <para>
/// Implementations are NOT registered in DI — they are created with <c>new</c> inside
/// <see cref="AgentJobDispatcher"/> and selected by dispatch type at runtime.
/// </para>
/// </summary>
internal interface IDispatchPreparationHandler
{
    /// <summary>
    /// Prepares the <see cref="AgentJobDispatcher.DispatchPipelineResult"/> for a dispatch.
    /// Receives the outputs of <see cref="AgentJobDispatcher"/>'s shared prologue
    /// (project, profile, agentProviderId) and returns the populated context, message
    /// customizer, and optional success callback — or <c>null</c> to abort the dispatch.
    /// </summary>
    /// <param name="project">The resolved project (non-null, ensured by the template).</param>
    /// <param name="profile">The resolved agent profile.</param>
    /// <param name="agentProviderId">The agent provider config ID from the profile.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    /// A populated <see cref="AgentJobDispatcher.DispatchPipelineResult"/> or <c>null</c>
    /// to abort the dispatch (run creation failed, config resolution failed, etc.).
    /// </returns>
    Task<AgentJobDispatcher.DispatchPipelineResult?> PrepareAsync(
        PipelineProject project,
        AgentProfile profile,
        string agentProviderId,
        CancellationToken ct);
}