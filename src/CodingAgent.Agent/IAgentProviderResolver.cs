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
    /// <param name="projectRepoFactory">
    /// Creates the providers for the project repositories a project epic clones next to its own.
    /// They must use the read-only token vended into their own provider config, because the
    /// orchestrator proxy's token refresh covers only the job's primary repository.
    /// </param>
    Task<ResolvedProviders> ResolveAsync(
        JobAssignmentMessage job,
        IProviderFactory providerFactory,
        IProviderFactory projectRepoFactory,
        ProviderConfig repoConfig,
        ProviderConfig agentConfig,
        CancellationToken ct);
}
