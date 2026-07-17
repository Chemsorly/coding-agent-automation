using System.Text.Json;
using System.Text.Json.Serialization;
using MessagePack;
using MessagePack.Formatters;

namespace CodingAgentWebUI.Pipeline.Models;

/// <summary>
/// Strongly-typed wrapper for provider configuration IDs.
/// Prevents accidental transposition of string parameters in method signatures.
/// </summary>
[JsonConverter(typeof(ProviderConfigIdJsonConverter))]
public readonly record struct ProviderConfigId(string Value)
{
    // TODO: Consider adding null/empty validation (e.g., ArgumentException.ThrowIfNullOrEmpty(value))
    // in the implicit conversion operator. Currently, null strings are silently wrapped into a
    // ProviderConfigId with Value = null, which can cause NullReferenceExceptions downstream
    // (e.g., in GetProviderConfigByIdAsync). All current callers validate at a higher level,
    // but defense-in-depth is lost at this boundary.
    public static implicit operator ProviderConfigId(string value) => new(value);
    public override string ToString() => Value;
}

/// <summary>
/// Custom JSON converter that serializes <see cref="ProviderConfigId"/> as a bare string,
/// maintaining wire compatibility with existing JSONB payloads that store provider IDs as strings.
/// Without this converter, System.Text.Json would serialize the struct as <c>{"value":"..."}</c>
/// instead of a plain string.
/// </summary>
internal sealed class ProviderConfigIdJsonConverter : JsonConverter<ProviderConfigId>
{
    // TODO: reader.GetString() returns null for JSON null tokens. The null-forgiving operator
    // silently wraps null into ProviderConfigId.Value, deferring the failure to downstream access.
    // Consider adding: if (value is null) throw new JsonException("ProviderConfigId cannot be null");
    // to fail fast on corrupt/adversarial payloads at the deserialization boundary.
    public override ProviderConfigId Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var value = reader.GetString();
        return new ProviderConfigId(value!);
    }

    public override void Write(Utf8JsonWriter writer, ProviderConfigId value, JsonSerializerOptions options)
    {
        writer.WriteStringValue(value.Value);
    }
}

/// <summary>
/// Custom MessagePack formatter that serializes <see cref="ProviderConfigId"/> as a bare string
/// on the wire, maintaining wire compatibility with existing MessagePack-serialized data.
/// Without this formatter, <c>ContractlessStandardResolver</c> would serialize the struct
/// as a map <c>{"Value":"..."}</c> instead of a plain string.
/// </summary>
public sealed class ProviderConfigIdFormatter : IMessagePackFormatter<ProviderConfigId>
{
    // TODO: writer.Write(value.Value) will write MessagePack nil if Value is null (e.g., default-
    // constructed struct). This silently produces nil for a non-nullable ProviderConfigId field,
    // which round-trips as ProviderConfigId(null!). Consider guarding: if (value.Value is null)
    // throw new MessagePackSerializationException("ProviderConfigId.Value cannot be null");
    public void Serialize(ref MessagePackWriter writer, ProviderConfigId value, MessagePackSerializerOptions options)
        => writer.Write(value.Value);

    // TODO: reader.ReadString() can return null for MessagePack nil tokens. The null-forgiving
    // operator silently wraps null into ProviderConfigId.Value. Consider adding a null check to
    // fail fast on corrupt payloads from misconfigured or compromised agent clients.
    public ProviderConfigId Deserialize(ref MessagePackReader reader, MessagePackSerializerOptions options)
        => new(reader.ReadString()!);
}

/// <summary>
/// MessagePack formatter for nullable <see cref="ProviderConfigId"/> values.
/// Serializes null as MessagePack nil and non-null as a bare string.
/// </summary>
public sealed class NullableProviderConfigIdFormatter : IMessagePackFormatter<ProviderConfigId?>
{
    public void Serialize(ref MessagePackWriter writer, ProviderConfigId? value, MessagePackSerializerOptions options)
    {
        if (value is null)
            writer.WriteNil();
        else
            writer.Write(value.Value.Value);
    }

    public ProviderConfigId? Deserialize(ref MessagePackReader reader, MessagePackSerializerOptions options)
    {
        if (reader.TryReadNil())
            return null;
        return new ProviderConfigId(reader.ReadString()!);
    }
}
