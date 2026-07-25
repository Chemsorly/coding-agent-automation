namespace CodingAgentWebUI.Pipeline.Models;

/// <summary>
/// Strongly-typed wrapper for agent IDs.
/// Prevents accidental transposition of string parameters in method signatures
/// (e.g., IAgentCancellationSender.SendCancelJobAsync has agentId and runId as consecutive string params).
/// Used as the canonical agent identifier type throughout the system (DI registration, constructor injection).
/// </summary>
// TODO: The primary constructor does not validate its input — new AgentId(null!) or default(AgentId)
// produces an instance with Value == null, bypassing the validation in the implicit conversion operator.
// Consider adding a constructor guard or a factory method to ensure Value is never null/empty.
public readonly record struct AgentId(string Value)
{
    public static implicit operator AgentId(string value)
    {
        ArgumentException.ThrowIfNullOrEmpty(value);
        return new(value);
    }

    public override string ToString() => Value;
}
