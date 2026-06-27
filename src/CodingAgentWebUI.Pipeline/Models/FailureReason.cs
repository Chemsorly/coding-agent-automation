namespace CodingAgentWebUI.Pipeline.Models;

/// <summary>
/// Classifies why a work item failed, enabling targeted retry/alerting logic.
/// </summary>
public enum FailureReason
{
    /// <summary>Work item exceeded its configured timeout.</summary>
    Timeout,

    /// <summary>Infrastructure-level failure (K8s Job creation, pod scheduling, OOM).</summary>
    InfrastructureFailure,

    /// <summary>Agent reported an error during execution.</summary>
    AgentError,

    /// <summary>Token refresh failed, agent lost access to provider APIs.</summary>
    TokenRefreshFailure
}
