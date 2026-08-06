using MessagePack;
using MessagePack.Formatters;

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

    public override string ToString() => Value ?? string.Empty;
}

/// <summary>
/// Custom MessagePack formatter that serializes <see cref="AgentId"/> as a bare string
/// on the wire, maintaining wire compatibility with agent callers that pass plain strings via
/// <c>InvokeAsync("DeregisterAgent", agentId)</c> and <c>InvokeAsync("AgentReady", agentId)</c>.
/// Without this formatter, <c>ContractlessStandardResolver</c> would serialize the struct
/// as a map <c>{"Value":"..."}</c> instead of a plain string, causing a
/// <c>MessagePackSerializationException</c> on every <c>DeregisterAgent</c> and <c>AgentReady</c>
/// hub invocation.
/// </summary>
public sealed class AgentIdFormatter : IMessagePackFormatter<AgentId>
{
    public void Serialize(ref MessagePackWriter writer, AgentId value, MessagePackSerializerOptions options)
    {
        if (value.Value is null)
            throw new MessagePackSerializationException("AgentId cannot serialize a null Value (e.g., default(AgentId)).");
        writer.Write(value.Value);
    }

    public AgentId Deserialize(ref MessagePackReader reader, MessagePackSerializerOptions options)
    {
        var value = reader.ReadString();
        if (value is null)
            throw new MessagePackSerializationException("AgentId cannot be deserialized from a nil token.");
        return new(value);
    }
}
