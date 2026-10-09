namespace CodingAgent.Kubernetes;

/// <summary>
/// Kubernetes Job condition type names and status values used to determine Job terminal state.
/// These are external API contract strings from the Kubernetes Batch API.
/// </summary>
public static class KubernetesJobConditions
{
    /// <summary>Condition type set by Kubernetes when a Job completes successfully.</summary>
    public const string JobPhaseComplete = "Complete";

    /// <summary>Condition type set by Kubernetes when a Job fails.</summary>
    public const string JobPhaseFailed = "Failed";

    /// <summary>Condition status value indicating the condition is active/true.</summary>
    public const string ConditionTrue = "True";
}
