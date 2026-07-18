namespace CodingAgentWebUI.Pipeline.Models;

/// <summary>
/// Strongly-typed wrapper for pipeline run IDs.
/// Prevents accidental transposition of string parameters in method signatures
/// (e.g., RunLifecycleManager.AgentAcceptedRunAsync has 3 consecutive string params).
/// </summary>
public readonly record struct RunId(string Value)
{
    // TODO: Consider adding ArgumentException.ThrowIfNullOrEmpty(value) in the implicit conversion
    // operator for defense-in-depth. Currently null strings are silently wrapped, deferring failure
    // to ThrowIfNullOrEmpty deeper in the call chain. Mirrors known issue in ProviderConfigId.
    public static implicit operator RunId(string value) => new(value);
    // TODO: Consider returning Value ?? string.Empty to satisfy the .NET contract that ToString()
    // returns a non-null string. default(RunId) currently produces null from ToString().
    public override string ToString() => Value;
}
