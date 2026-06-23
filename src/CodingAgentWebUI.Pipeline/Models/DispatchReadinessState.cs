namespace CodingAgentWebUI.Pipeline.Models;

/// <summary>
/// Readiness state for an issue in the dispatch drawer.
/// Named DispatchReadinessState to avoid conflict with the Kubernetes ReadinessState class.
/// </summary>
public enum DispatchReadinessState
{
    Unknown,
    Checking,
    Ready,
    Blocked,
    Queued
}
