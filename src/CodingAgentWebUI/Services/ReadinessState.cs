namespace CodingAgentWebUI.Services;

/// <summary>
/// Thread-safe readiness state for the /readyz Kubernetes probe.
/// Flipped to not-ready during graceful shutdown drain period so Kubernetes
/// removes the pod from Service endpoints before the process exits.
/// Registered as a singleton.
/// </summary>
public sealed class ReadinessState
{
    private volatile bool _isReady = true;

    /// <summary>
    /// Returns <c>true</c> when the application is ready to serve traffic.
    /// Returns <c>false</c> after graceful shutdown drain has begun.
    /// </summary>
    public bool IsReady => _isReady;

    /// <summary>
    /// Marks the application as not ready. Called once during shutdown drain.
    /// </summary>
    public void MarkNotReady() => _isReady = false;
}
