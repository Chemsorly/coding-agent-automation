using System.Reflection;
using System.Text.Json;
using AwesomeAssertions;
using CodingAgentWebUI.Pipeline;
using CodingAgentWebUI.Pipeline.Models;

namespace CodingAgentWebUI.Pipeline.UnitTests.Serialization;

/// <summary>
/// Validates that ALL enum values in the Pipeline domain survive JSON roundtrip serialization.
/// Guards against: renamed enum members losing their string representation, missing JsonStringEnumConverter,
/// and new enum values that serialize to their numeric form instead of string form.
/// 
/// Uses reflection to auto-discover all public enums in the Pipeline assembly. When a new enum
/// is added, it is automatically covered without requiring a new test method.
/// 
/// These tests exercise PipelineJsonOptions.Default (which includes JsonStringEnumConverter).
/// If any enum value fails roundtrip, the serialized config files on disk become unreadable
/// after a code change — a silent data corruption bug.
/// </summary>
public class PipelineEnumJsonRoundtripTests
{
    private static readonly JsonSerializerOptions Options = PipelineJsonOptions.Default;

    /// <summary>
    /// Discovers all public enum types in the Pipeline assembly and verifies every value
    /// roundtrips correctly (serialize → deserialize == identity) and serializes as a
    /// quoted string (not a bare integer).
    /// </summary>
    [Theory]
    [MemberData(nameof(AllPipelineEnumValues))]
    public void AllEnums_AllValues_RoundtripAsString(Type enumType, object value)
    {
        // Serialize
        var json = JsonSerializer.Serialize(value, enumType, Options);

        // Assert serializes as quoted string, not a bare number
        json.Should().StartWith("\"",
            $"{enumType.Name}.{value} should serialize as string, got: {json}");

        // Deserialize and verify roundtrip
        var deserialized = JsonSerializer.Deserialize(json, enumType, Options);
        deserialized.Should().Be(value,
            $"roundtrip failed for {enumType.Name}.{value}");
    }

    /// <summary>
    /// Guard: at least the 16 known enum types are discovered. If this count drops,
    /// an enum was removed or moved out of the Pipeline assembly without updating consumers.
    /// </summary>
    [Fact]
    public void Discovery_FindsAtLeastKnownEnumCount()
    {
        var enumTypes = GetPipelineEnumTypes();
        enumTypes.Should().HaveCountGreaterThanOrEqualTo(16,
            "expected at least 16 public enums in the Pipeline assembly (regression guard)");
    }

    // ── Data source ──────────────────────────────────────────────────────

    public static IEnumerable<object[]> AllPipelineEnumValues()
    {
        foreach (var enumType in GetPipelineEnumTypes())
        {
            foreach (var value in Enum.GetValues(enumType))
            {
                yield return [enumType, value];
            }
        }
    }

    private static IReadOnlyList<Type> GetPipelineEnumTypes()
    {
        var assembly = typeof(PipelineStep).Assembly;
        return assembly.GetExportedTypes()
            .Where(t => t.IsEnum)
            .OrderBy(t => t.FullName)
            .ToList();
    }
}
