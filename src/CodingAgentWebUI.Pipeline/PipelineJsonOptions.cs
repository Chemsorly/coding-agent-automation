using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CodingAgentWebUI.Pipeline;

/// <summary>
/// Shared JSON serialization options used across all pipeline layers.
/// Provides presets for both serialization (Default) and deserialization (Lenient) scenarios.
/// </summary>
public static class PipelineJsonOptions
{
    /// <summary>
    /// Standard options for serialization: camelCase, indented, enum-as-string, TimeSpan support.
    /// </summary>
    public static JsonSerializerOptions Default { get; } = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
        Converters = { new TimeSpanJsonConverter(), new JsonStringEnumConverter() }
    };

    /// <summary>
    /// Lenient options for deserialization: case-insensitive property matching, enum-as-string.
    /// </summary>
    public static JsonSerializerOptions Lenient { get; } = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    /// <summary>
    /// Custom converter for TimeSpan since System.Text.Json doesn't natively support it.
    /// Serializes as ISO 8601 duration string (e.g., "00:30:00").
    /// </summary>
    internal sealed class TimeSpanJsonConverter : JsonConverter<TimeSpan>
    {
        public override TimeSpan Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            var value = reader.GetString();
            return value is not null ? TimeSpan.Parse(value, CultureInfo.InvariantCulture) : default;
        }

        public override void Write(Utf8JsonWriter writer, TimeSpan value, JsonSerializerOptions options)
        {
            writer.WriteStringValue(value.ToString("c", CultureInfo.InvariantCulture));
        }
    }
}
