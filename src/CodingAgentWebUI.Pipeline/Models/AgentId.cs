using MessagePack;
using MessagePack.Formatters;

namespace CodingAgentWebUI.Pipeline.Models;

/// <summary>
/// Strongly-typed wrapper for agent IDs.
/// Prevents accidental transposition of string parameters in method signatures
/// (e.g., IAgentCancellationSender.SendCancelJobAsync has agentId and runId as consecutive string params).
/// Used as the canonical agent identifier type throughout the system (DI registration, constructor injection).
/// </summary>
/// <remarks>
/// <para><b>Construction invariant:</b> <c>new AgentId(value)</c> and the implicit <c>(AgentId)string</c>
/// operator both reject null and empty strings via <see cref="ArgumentException.ThrowIfNullOrEmpty"/>.
/// <c>default(AgentId)</c> is exempt — C# struct zero-initialization produces <c>Value = null</c>
/// and cannot be prevented. Use <c>default(AgentId)</c> only as a sentinel/unset value,
/// never as a valid agent identifier.</para>
/// </remarks>
public readonly record struct AgentId
{
    public string Value { get; init; }

    public AgentId(string value)
    {
        ArgumentException.ThrowIfNullOrEmpty(value);
        Value = value;
    }

    // TODO: Switching from a positional primary constructor to this explicit constructor removes the
    // compiler-synthesised Deconstruct(out string Value) method that record structs generate for
    // positional members. If any code uses positional deconstruction (var (val) = agentId; or a
    // property pattern case AgentId(var v)) it will fail to compile. No such callers were found
    // at the time of this change, but add `public void Deconstruct(out string value) => value = Value;`
    // if positional deconstruction is ever needed.
    public static implicit operator AgentId(string value)
    {
        ArgumentException.ThrowIfNullOrEmpty(value);
        return new(value);
    }

    public override string ToString() => Value ?? string.Empty;
}

/// <summary>
/// Custom MessagePack formatter that serializes <see cref="AgentId"/> as a bare string
/// on the wire, maintaining wire compatibility with existing string-based callers.
/// Without this formatter, <c>ContractlessStandardResolver</c> would serialize the struct
/// as a map <c>{"Value":"..."}</c> instead of a plain string.
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
        if (string.IsNullOrEmpty(value))
            throw new MessagePackSerializationException("AgentId cannot be deserialized from an empty string.");
        return new(value);
    }
}
