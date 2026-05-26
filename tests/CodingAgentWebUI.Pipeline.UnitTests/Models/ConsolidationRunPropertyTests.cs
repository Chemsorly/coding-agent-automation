// Feature: 021-consolidation-loops
// Property 1: ConsolidationRun Serialization Round-Trip
// Property 2: HarnessSuggestions Serialization Round-Trip
using System.Text.Json;
using System.Text.Json.Serialization;
using AwesomeAssertions;
using FsCheck;
using FsCheck.Fluent;
using FsCheck.Xunit;
using CodingAgentWebUI.Pipeline.Models;

namespace CodingAgentWebUI.Pipeline.UnitTests.Models;

/// <summary>
/// Property-based tests for ConsolidationRun and HarnessSuggestions JSON serialization round-trips.
/// **Validates: Requirements 3.2, 8.1**
/// </summary>
public class ConsolidationRunPropertyTests
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() }
    };

    /// <summary>
    /// Property 1: ConsolidationRun Serialization Round-Trip
    /// For any valid ConsolidationRun instance, serializing to JSON and deserializing back
    /// SHALL produce a structurally equivalent object with all fields preserved.
    /// **Validates: Requirements 3.2**
    /// </summary>
    [Property(Arbitrary = new[] { typeof(ConsolidationRunArbitraries) })]
    public void ConsolidationRun_JsonRoundTrip_PreservesAllFields(ConsolidationRun original)
    {
        var json = JsonSerializer.Serialize(original, JsonOptions);
        var deserialized = JsonSerializer.Deserialize<ConsolidationRun>(json, JsonOptions);

        deserialized.Should().NotBeNull();
        deserialized!.RunId.Should().Be(original.RunId);
        deserialized.Type.Should().Be(original.Type);
        deserialized.TemplateId.Should().Be(original.TemplateId);
        deserialized.TemplateName.Should().Be(original.TemplateName);
        deserialized.StartedAtUtc.Should().Be(original.StartedAtUtc);
        deserialized.CompletedAtUtc.Should().Be(original.CompletedAtUtc);
        deserialized.Status.Should().Be(original.Status);
        deserialized.Summary.Should().Be(original.Summary);
    }

    /// <summary>
    /// Property 2: HarnessSuggestions Serialization Round-Trip
    /// For any valid HarnessSuggestions instance with arbitrary suggestions list,
    /// serializing to JSON and deserializing back SHALL produce a structurally equivalent object.
    /// **Validates: Requirements 8.1**
    /// </summary>
    [Property(Arbitrary = new[] { typeof(ConsolidationRunArbitraries) })]
    public void HarnessSuggestions_JsonRoundTrip_PreservesAllFields(HarnessSuggestions original)
    {
        var json = JsonSerializer.Serialize(original, JsonOptions);
        var deserialized = JsonSerializer.Deserialize<HarnessSuggestions>(json, JsonOptions);

        deserialized.Should().NotBeNull();
        deserialized!.GeneratedAtUtc.Should().Be(original.GeneratedAtUtc);
        deserialized.BasedOnRunCount.Should().Be(original.BasedOnRunCount);
        deserialized.SuccessRate.Should().Be(original.SuccessRate);
        deserialized.Suggestions.Should().HaveCount(original.Suggestions.Count);

        for (var i = 0; i < original.Suggestions.Count; i++)
        {
            deserialized.Suggestions[i].Text.Should().Be(original.Suggestions[i].Text);
            deserialized.Suggestions[i].Rationale.Should().Be(original.Suggestions[i].Rationale);
            deserialized.Suggestions[i].Frequency.Should().Be(original.Suggestions[i].Frequency);
        }
    }
}

/// <summary>
/// FsCheck arbitrary generators for consolidation models.
/// </summary>
public class ConsolidationRunArbitraries
{
    private static readonly string[] RunIdPool = Enumerable.Range(0, 10)
        .Select(_ => Guid.NewGuid().ToString()).ToArray();

    private static readonly string[] TemplateIdPool =
        ["tmpl-aaa", "tmpl-bbb", "tmpl-ccc"];

    private static readonly string[] TemplateNamePool =
        ["DotNet Repo", "Python Repo", "Global", "Java Service"];

    private static readonly string[] SummaryPool =
        ["Completed successfully", "No changes needed", "Failed: timeout", "Created 2 issues", "Generated 3 suggestions"];

    private static readonly string[] SuggestionTextPool =
        ["Add retry logic", "Improve error messages", "Cache results", "Add timeout", "Validate inputs"];

    private static readonly string[] RationalePool =
        ["Seen in 5 failures", "Common pattern", "Reduces latency", "Prevents data loss"];

    public static Arbitrary<ConsolidationRun> ConsolidationRunArb()
    {
        var gen =
            from runId in Gen.Elements(RunIdPool)
            from type in Gen.Elements(
                ConsolidationRunType.BrainConsolidation,
                ConsolidationRunType.RefactoringDetection,
                ConsolidationRunType.HarnessSuggestions)
            from hasTemplateId in Gen.Elements(true, false)
            from templateId in Gen.Elements(TemplateIdPool)
            from hasTemplateName in Gen.Elements(true, false)
            from templateName in Gen.Elements(TemplateNamePool)
            from startedAt in Gen.Elements(
                new DateTime(2025, 1, 15, 10, 0, 0, DateTimeKind.Utc),
                new DateTime(2025, 6, 20, 14, 30, 0, DateTimeKind.Utc),
                new DateTime(2026, 3, 5, 8, 45, 0, DateTimeKind.Utc))
            from hasCompleted in Gen.Elements(true, false)
            from status in Gen.Elements(
                ConsolidationRunStatus.Running,
                ConsolidationRunStatus.Succeeded,
                ConsolidationRunStatus.Failed,
                ConsolidationRunStatus.Queued,
                ConsolidationRunStatus.Cancelled)
            from hasSummary in Gen.Elements(true, false)
            from summary in Gen.Elements(SummaryPool)
            select new ConsolidationRun
            {
                RunId = runId,
                Type = type,
                TemplateId = hasTemplateId ? templateId : null,
                TemplateName = hasTemplateName ? templateName : null,
                StartedAtUtc = startedAt,
                CompletedAtUtc = hasCompleted ? startedAt.AddMinutes(5) : null,
                Status = status,
                Summary = hasSummary ? summary : null
            };

        return gen.ToArbitrary();
    }

    public static Arbitrary<HarnessSuggestions> HarnessSuggestionsArb()
    {
        var suggestionGen =
            from text in Gen.Elements(SuggestionTextPool)
            from rationale in Gen.Elements(RationalePool)
            from freq in Gen.Choose(1, 20)
            select new HarnessSuggestion { Text = text, Rationale = rationale, Frequency = freq };

        var gen =
            from generatedAt in Gen.Elements(
                new DateTime(2025, 1, 15, 10, 0, 0, DateTimeKind.Utc),
                new DateTime(2025, 6, 20, 14, 30, 0, DateTimeKind.Utc),
                new DateTime(2026, 3, 5, 8, 45, 0, DateTimeKind.Utc))
            from runCount in Gen.Choose(1, 200)
            from successRate in Gen.Choose(0, 100).Select(x => (decimal)x)
            from suggestionCount in Gen.Choose(0, 5)
            from suggestions in Gen.ArrayOf(suggestionGen, suggestionCount)
            select new HarnessSuggestions
            {
                GeneratedAtUtc = generatedAt,
                BasedOnRunCount = runCount,
                SuccessRate = successRate,
                Suggestions = suggestions.ToList()
            };

        return gen.ToArbitrary();
    }
}
