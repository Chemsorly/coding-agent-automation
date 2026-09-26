using CodingAgent.Pipeline.Models;

namespace CodingAgent.Pipeline.Interfaces;

/// <summary>
/// Resolves the agent selector labels for a consolidation dispatch request.
/// Extracted from <c>ConsolidationDispatcher.ResolveSelector</c> so that selector logic
/// (which depends on <see cref="IAgentProfileStore"/> and <see cref="IPipelineConfigStore"/>)
/// stays in <c>CodingAgent.Web</c> while <c>ConsolidationService</c> (in <c>CodingAgent.Pipeline</c>)
/// can call it without taking on infrastructure-layer dependencies.
/// </summary>
public interface IConsolidationSelectorResolver
{
    /// <summary>
    /// Resolves the agent selector labels for a consolidation run.
    /// </summary>
    /// <param name="repoConfig">
    /// The repo <see cref="ProviderConfig"/> resolved at trigger time, used to read
    /// <see cref="ProviderConfig.RequiredLabels"/>. May be <c>null</c> for global runs.
    /// </param>
    /// <param name="config">
    /// The static <see cref="PipelineConfiguration"/> from application startup, used as
    /// a fallback source for <see cref="PipelineConfiguration.DefaultRequiredAgentLabels"/>.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    /// The resolved selector labels, or <c>null</c> when no agent profiles are available
    /// (startup race — the caller must treat this as a transient failure, NOT a permanent 422).
    /// </returns>
    Task<IReadOnlyList<string>?> ResolveAsync(
        ProviderConfig? repoConfig,
        PipelineConfiguration config,
        CancellationToken ct);
}
