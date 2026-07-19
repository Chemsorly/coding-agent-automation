using System.Text.Json;
using System.Text.Json.Serialization;

namespace CodingAgentWebUI.Pipeline.Models;

/// <summary>
/// Strongly-typed wrapper for issue identifiers (e.g., "owner/repo#123").
/// Prevents accidental transposition of string parameters in method signatures.
/// </summary>
/// <remarks>
/// Unlike <see cref="ProviderConfigId"/>/<see cref="RunId"/>/<see cref="AgentId"/> which only have
/// a unidirectional implicit conversion (string → ValueType), this type includes a bidirectional
/// implicit conversion (ValueType → string as well). This is necessary because the type is adopted
/// on <see cref="PipelineRun.IssueIdentifier"/> which is consumed in 42+ source files that pass
/// the value to string-typed APIs. The reverse implicit keeps the cascade manageable for Phase 1.
/// Phase 2 can remove the reverse implicit incrementally as callers are updated.
/// </remarks>
[JsonConverter(typeof(IssueIdentifierJsonConverter))]
public readonly record struct IssueIdentifier(string Value)
{
    /// <summary>Implicit conversion from string to IssueIdentifier.</summary>
    public static implicit operator IssueIdentifier(string value) => new(value);

    /// <summary>
    /// Implicit conversion from IssueIdentifier to string.
    /// Enables transparent usage at consumption sites without requiring .Value.
    /// </summary>
    public static implicit operator string(IssueIdentifier id) => id.Value;

    /// <inheritdoc/>
    public override string ToString() => Value;

    /// <summary>
    /// Custom JSON converter that serializes <see cref="IssueIdentifier"/> as a bare string
    /// (not as <c>{"value":"..."}</c>) to maintain wire compatibility with existing JSONB payloads.
    /// </summary>
    internal sealed class IssueIdentifierJsonConverter : JsonConverter<IssueIdentifier>
    {
        public override IssueIdentifier Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
            => new(reader.GetString()!);

        public override void Write(Utf8JsonWriter writer, IssueIdentifier value, JsonSerializerOptions options)
            => writer.WriteStringValue(value.Value);
    }
}
