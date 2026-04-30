using System.Text.Json;
using System.Text.Json.Serialization;
using AwesomeAssertions;
using FsCheck;
using FsCheck.Fluent;
using FsCheck.Xunit;
using MessagePack;
using CodingAgentWebUI.Pipeline.Models;

namespace CodingAgentWebUI.Pipeline.UnitTests;

/// <summary>
/// Property-based tests for ReviewerConfiguration serialization round-trips.
/// Feature: 014-reviewer-configuration-ui
/// </summary>
public class ReviewerSerializationPropertyTests
{
    /// <summary>
    /// Recreates the same JsonSerializerOptions used by JsonConfigurationStore.
    /// (camelCase, indented, TimeSpanConverter, StringEnumConverter)
    /// </summary>
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
        Converters = { new TimeSpanJsonConverter(), new JsonStringEnumConverter() }
    };

    private static readonly MessagePackSerializerOptions MsgPackOptions =
        MessagePackSerializerOptions.Standard.WithResolver(
            MessagePack.Resolvers.ContractlessStandardResolver.Instance);

    /// <summary>
    /// Property 1: JSON Serialization Round-Trip
    /// For any valid ReviewerConfiguration instance, serializing to JSON and deserializing back
    /// SHALL produce an object with identical field values using record equality.
    /// **Validates: Requirements 1.6, 2.2**
    /// </summary>
    [Property(MaxTest = 100, Arbitrary = new[] { typeof(ReviewerSerializationArbitraries) })]
    public void JsonRoundTrip_SerializeDeserialize_ProducesEqualObject(ReviewerConfiguration original)
    {
        var json = JsonSerializer.Serialize(original, JsonOptions);
        var deserialized = JsonSerializer.Deserialize<ReviewerConfiguration>(json, JsonOptions);

        deserialized.Should().NotBeNull();
        deserialized!.Id.Should().Be(original.Id);
        deserialized.DisplayName.Should().Be(original.DisplayName);
        deserialized.MatchLabels.Should().BeEquivalentTo(original.MatchLabels);
        deserialized.Enabled.Should().Be(original.Enabled);
        deserialized.ExecutionOrder.Should().Be(original.ExecutionOrder);
        deserialized.Agents.Count.Should().Be(original.Agents.Count);

        for (int i = 0; i < original.Agents.Count; i++)
        {
            deserialized.Agents[i].Name.Should().Be(original.Agents[i].Name);
            deserialized.Agents[i].Prompt.Should().Be(original.Agents[i].Prompt);
        }
    }

    /// <summary>
    /// Property 2: MessagePack Serialization Round-Trip
    /// For any valid ReviewerConfiguration instance, serializing to MessagePack and deserializing back
    /// SHALL produce an object with identical field values.
    /// **Validates: Requirements 1.5, 1.6**
    /// </summary>
    [Property(MaxTest = 100, Arbitrary = new[] { typeof(ReviewerSerializationArbitraries) })]
    public void MessagePackRoundTrip_SerializeDeserialize_ProducesEqualObject(ReviewerConfiguration original)
    {
        var bytes = MessagePackSerializer.Serialize(original, MsgPackOptions);
        var deserialized = MessagePackSerializer.Deserialize<ReviewerConfiguration>(bytes, MsgPackOptions);

        deserialized.Should().NotBeNull();
        deserialized!.Id.Should().Be(original.Id);
        deserialized.DisplayName.Should().Be(original.DisplayName);
        deserialized.MatchLabels.Should().BeEquivalentTo(original.MatchLabels);
        deserialized.Enabled.Should().Be(original.Enabled);
        deserialized.ExecutionOrder.Should().Be(original.ExecutionOrder);
        deserialized.Agents.Count.Should().Be(original.Agents.Count);

        for (int i = 0; i < original.Agents.Count; i++)
        {
            deserialized.Agents[i].Name.Should().Be(original.Agents[i].Name);
            deserialized.Agents[i].Prompt.Should().Be(original.Agents[i].Prompt);
        }
    }

    /// <summary>
    /// TimeSpan converter matching JsonConfigurationStore's private implementation.
    /// Serializes TimeSpan as ISO 8601 duration string (e.g., "00:30:00").
    /// </summary>
    private sealed class TimeSpanJsonConverter : JsonConverter<TimeSpan>
    {
        public override TimeSpan Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            var value = reader.GetString();
            return TimeSpan.Parse(value!, System.Globalization.CultureInfo.InvariantCulture);
        }

        public override void Write(Utf8JsonWriter writer, TimeSpan value, JsonSerializerOptions options)
        {
            writer.WriteStringValue(value.ToString("c", System.Globalization.CultureInfo.InvariantCulture));
        }
    }
}

/// <summary>
/// FsCheck arbitrary generators for ReviewerConfiguration serialization property tests.
/// Generates valid ReviewerConfiguration instances with edge cases:
/// - 1-5 agents per config
/// - Agent names from a pool
/// - Prompts including long strings (100+ chars), unicode characters, newlines
/// - Various MatchLabels combinations (empty, 1-3 labels)
/// - Random Enabled/ExecutionOrder values
/// </summary>
public class ReviewerSerializationArbitraries
{
    private static readonly string[] AgentNamePool =
        ["Correctness", "Security", "DotNetSpecialist", "PythonLinter", "Performance"];

    private static readonly string[] LabelPool =
        ["kiro", "dotnet", "python", "java", "node", "csharp", "python312", "java21"];

    private static readonly string[] UnicodeStrings =
    [
        "Review für Korrektheit 🔍",
        "检查安全问题",
        "パフォーマンスレビュー",
        "Проверка кода на ошибки",
        "مراجعة الأمان",
        "Vérifier les problèmes de sécurité",
        "코드 리뷰 에이전트"
    ];

    private static readonly string[] SpecialCharStrings =
    [
        "Review with \"quotes\" and 'apostrophes'",
        "Check for issues:\n- Bug 1\n- Bug 2\n- Bug 3",
        "Path: C:\\Users\\dev\\project",
        "Tab\there\tand\tthere",
        "Backslash \\ and forward slash /",
        "Angle <brackets> & ampersands",
        "Null char test: before\0after"
    ];

    public static Arbitrary<ReviewerConfiguration> ReviewerConfigurationArb()
    {
        var promptGen = Gen.OneOf(
            // Short prompts
            Gen.Elements(
                "Review for correctness",
                "Check security issues",
                "Verify .NET patterns",
                "Lint Python code",
                "Check performance bottlenecks"),
            // Long prompts (100+ chars)
            Gen.Elements(
                new string('A', 150),
                "You are a specialized code reviewer. Your task is to review the code changes for correctness, security vulnerabilities, performance issues, and adherence to best practices. Pay special attention to error handling and edge cases. " + new string('x', 200),
                string.Join("\n", Enumerable.Range(1, 20).Select(i => $"- Check item {i}: verify that the implementation handles edge case {i} correctly and does not introduce regressions"))),
            // Unicode prompts
            Gen.Elements(UnicodeStrings),
            // Special character prompts
            Gen.Elements(SpecialCharStrings)
        );

        var agentGen =
            from name in Gen.Elements(AgentNamePool)
            from prompt in promptGen
            select new ReviewAgent { Name = name, Prompt = prompt };

        var labelSubsetGen = Gen.OneOf(
            Gen.Constant(Array.Empty<string>()).Select(a => (IReadOnlyList<string>)a.ToList()),  // empty (global)
            Gen.SubListOf(LabelPool).Select(l => (IReadOnlyList<string>)l.ToList()),             // random subset
            Gen.ArrayOf(Gen.Elements(LabelPool), 1).Select(a => (IReadOnlyList<string>)a.ToList()),  // single label
            Gen.ArrayOf(Gen.Elements(LabelPool), 3).Select(a => (IReadOnlyList<string>)a.ToList())   // 3 labels
        );

        var displayNameGen = Gen.OneOf(
            Gen.Elements("Global Reviewers", "DotNet Reviewers", "Python Reviewers",
                "Security Gate", "Performance Gate", "Java Reviewers"),
            Gen.Elements(UnicodeStrings.Take(3).ToArray()),
            Gen.Elements("Config with \"quotes\"", "Config\nwith\nnewlines")
        );

        var configGen =
            from id in Gen.Constant(Guid.NewGuid().ToString())
            from displayName in displayNameGen
            from matchLabels in labelSubsetGen
            from enabled in Gen.Elements(true, false)
            from executionOrder in Gen.Choose(0, 100)
            from agentCount in Gen.Choose(1, 5)
            from agents in Gen.ArrayOf(agentGen, agentCount)
            select new ReviewerConfiguration
            {
                Id = id,
                DisplayName = displayName,
                MatchLabels = matchLabels,
                Agents = agents.ToList(),
                Enabled = enabled,
                ExecutionOrder = executionOrder
            };

        return configGen.ToArbitrary();
    }
}
