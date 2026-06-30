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
}
