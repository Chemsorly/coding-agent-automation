namespace CodingAgent.Pipeline.Models;

/// <summary>
/// Shared constants for token refresh logic, used by both the agent-side
/// <c>OrchestratorProxy</c> and the server-side <c>AgentTokenRefreshService</c>.
/// Centralising the value here ensures both sides use the same proactive-renewal
/// threshold and any future adjustment is a single-line change.
/// </summary>
public static class TokenRefreshConstants
{
    /// <summary>
    /// How far before token expiry to proactively request a refresh.
    /// Used by <c>OrchestratorProxy</c> (agent-side cache renewal) and
    /// <c>AgentTokenRefreshService.VendTokenAsync</c> (server-side expiry gate).
    /// </summary>
    public static readonly TimeSpan RenewalBuffer = TimeSpan.FromMinutes(5);
}
