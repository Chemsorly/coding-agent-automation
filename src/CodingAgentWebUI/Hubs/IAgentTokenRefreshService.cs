using CodingAgentWebUI.Pipeline.Models;

namespace CodingAgentWebUI.Hubs;

/// <summary>
/// Handles token refresh logic for agents, supporting both SignalR mode (PipelineRun in memory)
/// and K8s mode (WorkItem payload in DB). Extracted from AgentHub.Pipeline.cs to keep the hub thin.
/// </summary>
public interface IAgentTokenRefreshService
{
    /// <summary>
    /// Resolves the provider configuration and generates/returns an appropriate token
    /// for the requested <paramref name="providerKind"/>.
    /// </summary>
    /// <param name="jobId">The job/run identifier.</param>
    /// <param name="providerKind">The kind of provider to generate a token for (Repository or Brain).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A token refresh response containing the token and expiration.</returns>
    Task<TokenRefreshResponse> RefreshTokenAsync(string jobId, ProviderKind providerKind, CancellationToken ct);
}
