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
// NOTE: The proliferation of AgentId usage across more call sites (post-#1759) increases the attack surface
// for this gap. Paths that bypass the implicit operator (direct new AgentId(...), explicit casts, reflection)
// can still produce AgentId with null Value and cause misleading NullReferenceException downstream.
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
/// on the wire, maintaining wire compatibility with string-based callers (Agent project).
/// Without this formatter, <c>ContractlessStandardResolver</c> would serialize the struct
/// as a map <c>{"Value":"..."}</c> instead of a plain string, breaking SignalR hub method binding.
/// Mirrors the <see cref="JobIdFormatter"/> pattern.
/// </summary>
// TODO: Add AgentIdFormatter unit tests in tests/CodingAgentWebUI.Pipeline.UnitTests/Models/AgentIdTests.cs
// mirroring JobIdFormatterTests: round-trip serialize/deserialize, bare-string serialization, null Value throws,
// nil token deserialization throws, and empty string deserialization behavior (see Deserialize TODO above).
// AgentIdFormatter is in the SignalR critical path for DeregisterAgent and AgentReady hub methods.
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
        // TODO: Also reject empty strings here to match the implicit operator's validation contract.
        // A MessagePack payload with an empty string token ("") deserializes successfully into AgentId(""),
        // bypassing the ArgumentException.ThrowIfNullOrEmpty guard in the implicit operator. An adversarial
        // agent could send DeregisterAgent or AgentReady with an empty agentId — the ownership check in
        // AgentHub would still reject it, but the type's own validation contract is not enforced at the
        // deserialization boundary. Fix: add ArgumentException.ThrowIfNullOrEmpty(value, nameof(value))
        // before return new(value).
        return new(value);
    }
}
