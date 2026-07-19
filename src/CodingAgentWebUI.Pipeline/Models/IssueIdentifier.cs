// TODO: Add unit tests for IssueIdentifier — the implicit conversion operator, custom JsonConverter
// (IssueIdentifierJsonConverter), and ToString() override have zero test coverage. The JSON converter
// is critical for backward-compatible JSONB deserialization of existing WorkItem rows.
// Also add tests for default(IssueIdentifier) / null-string edge cases and HashSet equality behavior.
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CodingAgentWebUI.Pipeline.Models;

/// <summary>
/// Strongly-typed wrapper for issue identifiers (e.g., "org/repo#42").
/// Prevents accidental transposition of string parameters in method signatures.
/// </summary>
[JsonConverter(typeof(IssueIdentifierJsonConverter))]
public readonly record struct IssueIdentifier(string Value)
{
    // TODO: Consider adding null/empty validation (e.g., ArgumentException.ThrowIfNullOrEmpty(value))
    // in the implicit conversion operator. Currently, null strings are silently wrapped into an
    // IssueIdentifier with Value = null. Combined with removed ArgumentNullException.ThrowIfNull guards
    // in PipelineOrchestrationService (CreateDispatchedRunAsync, ReserveRunIdAsync), null strings now
    // propagate as a struct instead of throwing early. Downstream .Value accesses (e.g., EF queries,
    // label swaps, issue API calls) will cause NullReferenceExceptions. ProviderConfigId has the same gap.
    public static implicit operator IssueIdentifier(string value) => new(value);
    public override string ToString() => Value;
}

/// <summary>
/// Custom JSON converter that serializes <see cref="IssueIdentifier"/> as a bare string
/// instead of the default <c>{"Value":"..."}</c> object representation.
/// Required for backward-compatible JSONB deserialization of existing WorkItem rows.
/// </summary>
public sealed class IssueIdentifierJsonConverter : JsonConverter<IssueIdentifier>
{
    public override IssueIdentifier Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        // TODO: reader.GetString() returns null for JSON null tokens; the null-forgiving operator suppresses
        // the warning but produces IssueIdentifier(null). Consider adding a null check or throwing JsonException
        // for null tokens to fail fast on corrupt/partially-migrated WorkItem JSONB rows rather than propagating
        // a structurally invalid IssueIdentifier. ProviderConfigId has the same gap.
        => new(reader.GetString()!);

    public override void Write(Utf8JsonWriter writer, IssueIdentifier value, JsonSerializerOptions options)
        => writer.WriteStringValue(value.Value);
}
