namespace CodingAgentWebUI.Pipeline.Models;

/// <summary>
/// Strongly-typed wrapper for agent IDs.
/// Prevents accidental transposition of string parameters in method signatures
/// (e.g., IAgentCancellationSender.SendCancelJobAsync has agentId and runId as consecutive string params).
/// </summary>
public readonly record struct AgentId(string Value)
{
    public static implicit operator AgentId(string value)
    {
        ArgumentException.ThrowIfNullOrEmpty(value);
        return new(value);
    }

    public override string ToString() => Value ?? string.Empty;
}
