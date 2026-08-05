using MessagePack;
using MessagePack.Formatters;

namespace CodingAgentWebUI.Pipeline.Models;

/// <summary>
/// Strongly-typed wrapper for agent IDs.
/// Prevents accidental transposition of string parameters in method signatures
/// (e.g., IAgentCancellationSender.SendCancelJobAsync has agentId and runId as consecutive string params).
/// Used as the canonical agent identifier type throughout the system (DI registration, constructor injection).
/// </summary>
public readonly record struct AgentId
{
    /// <summary>
    /// The underlying string value. Never null or empty for a valid instance.
    /// </summary>
    public string Value { get; }

    /// <summary>
    /// Constructs an <see cref="AgentId"/> with validation. Throws if <paramref name="value"/> is null or empty.
    /// </summary>
    public AgentId(string value)
    {
        ArgumentException.ThrowIfNullOrEmpty(value);
        Value = value;
    }

    public static implicit operator AgentId(string value)
    {
        ArgumentException.ThrowIfNullOrEmpty(value);
        return new(value);
    }

    public override string ToString() => Value ?? string.Empty;
}

/// <summary>
/// Custom MessagePack formatter that serializes <see cref="AgentId"/> as a bare string
/// on the wire, maintaining wire compatibility with agent clients that send plain strings.
/// Without this formatter, <c>ContractlessStandardResolver</c> would serialize the struct
/// as a map <c>{"Value":"..."}</c> instead of a plain string, causing deserialization failures
/// in SignalR hub methods that receive <see cref="AgentId"/> parameters (e.g.,
/// <c>DeregisterAgent</c> and <c>AgentReady</c> on <c>AgentHub</c>).
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
