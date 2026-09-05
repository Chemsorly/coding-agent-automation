namespace CodingAgentWebUI.Pipeline.Models;

/// <summary>
/// Strongly-typed wrapper for pipeline run IDs.
/// Prevents accidental transposition of string parameters in method signatures
/// (e.g., RunLifecycleManager.AgentAcceptedRunAsync has 3 consecutive string params).
/// </summary>
public readonly record struct RunId(string Value)
{
    public static implicit operator RunId(string value)
    {
        ArgumentException.ThrowIfNullOrEmpty(value);
        return new(value);
    }

    public override string ToString() => Value ?? string.Empty;
}
