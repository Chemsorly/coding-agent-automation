namespace CodingAgentWebUI.Pipeline.Models;

/// <summary>
/// Strongly-typed wrapper for agent IDs.
/// Prevents accidental transposition of string parameters in method signatures
/// (e.g., IAgentCancellationSender.SendCancelJobAsync has agentId and runId as consecutive string params).
/// </summary>
public readonly record struct AgentId(string Value)
{
    // TODO: Consider adding ArgumentException.ThrowIfNullOrEmpty(value) in the implicit conversion
    // operator for defense-in-depth. Currently null strings are silently wrapped, deferring failure
    // to ThrowIfNullOrEmpty deeper in the call chain. Mirrors known issue in ProviderConfigId/RunId.
    public static implicit operator AgentId(string value) => new(value);
    // TODO: Consider returning Value ?? string.Empty to satisfy the .NET contract that ToString()
    // returns a non-null string. default(AgentId) currently produces null from ToString().
    public override string ToString() => Value;
}
