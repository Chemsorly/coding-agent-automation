namespace CodingAgentWebUI.Services;

/// <summary>
/// Application-wide feature flags resolved at startup.
/// Always registered as a singleton regardless of deployment mode.
/// </summary>
public sealed class FeatureFlags
{
    /// <summary>
    /// True when the orchestrator is running in database-backed persistence mode
    /// (Database:Host is configured). False in JSON-file mode.
    /// </summary>
    public bool IsDatabaseMode { get; init; }

    /// <summary>
    /// True when the orchestrator is running in Kubernetes work distribution mode
    /// (WorkDistribution:Mode == "Kubernetes"). False in SignalR and Legacy modes.
    /// </summary>
    public bool IsKubernetesMode { get; init; }
}
