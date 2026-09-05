using System.Text.Json;
using System.Text.Json.Serialization;

namespace CodingAgentWebUI.Pipeline.Models;

/// <summary>
/// Controls whether review agents run in isolation.
/// Isolated is the zero value so that default(ReviewIsolation) matches the intended default
/// — MessagePack source-generated formatters use default(T) for missing array elements.
///
/// ReviewIsolation.Shared was removed in #2233 (decision #1042). The custom
/// <see cref="ReviewIsolationJsonConverter"/> maps any unknown string value (including the
/// legacy "Shared") to <see cref="Isolated"/> so that old stored JSON configs deserialize
/// without error. The [JsonConverter] attribute is on the enum type itself so that the
/// fallback applies regardless of which JsonSerializerOptions instance is in use.
/// </summary>
[JsonConverter(typeof(ReviewIsolationJsonConverter))]
public enum ReviewIsolation
{
    /// <summary>Review agents run in fresh sessions with no shared context (default).</summary>
    Isolated = 0,
}

/// <summary>
/// Custom JSON converter for <see cref="ReviewIsolation"/> that provides graceful migration
/// for stored configs that still contain the legacy "Shared" string value. Any unknown value
/// (including "Shared") maps to <see cref="ReviewIsolation.Isolated"/>.
///
/// Registered at the type level so that it takes precedence over any global
/// <see cref="JsonStringEnumConverter"/> in <see cref="PipelineJsonOptions"/>.
/// </summary>
public sealed class ReviewIsolationJsonConverter : JsonConverter<ReviewIsolation>
{
    public override ReviewIsolation Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        // TODO: reader.TokenType is not checked before calling GetString(). If the JSON value is
        // a number (e.g. "reviewIsolation": 0 or 1 — valid under the old JsonStringEnumConverter
        // with allowIntegerValues:true, or produced by round-tripping MessagePack-integer payloads
        // through JSON), GetString() throws InvalidOperationException. Add a
        // reader.TokenType == JsonTokenType.Number guard that reads the integer and maps it to
        // ReviewIsolation.Isolated to maintain the graceful-migration contract for integer inputs.
        var value = reader.GetString();
        if (string.Equals(value, nameof(ReviewIsolation.Isolated), StringComparison.OrdinalIgnoreCase))
            return ReviewIsolation.Isolated;

        // Unknown value (e.g. legacy "Shared") — map to Isolated for graceful migration.
        return ReviewIsolation.Isolated;
    }

    public override void Write(Utf8JsonWriter writer, ReviewIsolation value, JsonSerializerOptions options)
    {
        // Always write as a quoted string to match JsonStringEnumConverter behaviour.
        writer.WriteStringValue(nameof(ReviewIsolation.Isolated));
    }
}
