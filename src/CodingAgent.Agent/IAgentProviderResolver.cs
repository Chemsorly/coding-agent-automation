using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;

namespace CodingAgent.Agent;

/// <summary>
/// Resolves and validates provider instances (repository, agent, brain, pipeline) from a
/// <see cref="JobAssignmentMessage"/>.
/// </summary>
internal interface IAgentProviderResolver
{
    /// <summary>
    /// Resolves all providers needed for a pipeline run from the job assignment.
    /// On failure, disposes any partially-created providers before re-throwing.
    /// </summary>
    Task<ResolvedProviders> ResolveAsync(
        JobAssignmentMessage job,
        IProviderFactory providerFactory,
        ProviderConfig repoConfig,
        ProviderConfig agentConfig,
        CancellationToken ct);
}
