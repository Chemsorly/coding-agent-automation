// Feature: 021-consolidation-loops
// Property 6: Brain Consolidation Prompt Includes Timestamp
// Property 7: Brain Consolidation Summary Includes All Metrics
using AwesomeAssertions;
using FsCheck;
using FsCheck.Fluent;
using FsCheck.Xunit;
using CodingAgentWebUI.Pipeline.Services;

namespace CodingAgentWebUI.Pipeline.UnitTests.Services;

/// <summary>
/// Property-based tests for ConsolidationPromptBuilder.
/// **Validates: Requirements 4.3, 4.7**
/// </summary>
public class ConsolidationPromptBuilderPropertyTests
{
    /// <summary>
    /// Property 6: Brain Consolidation Prompt Includes Timestamp
    /// For any non-null DateTime, the prompt contains the formatted timestamp;
    /// for null, the prompt contains an indication of no prior consolidation.
    /// **Validates: Requirements 4.3**
    /// </summary>
    [Property(Arbitrary = new[] { typeof(PromptBuilderArbitraries) })]
    public void BrainConsolidationPrompt_NonNullTimestamp_ContainsFormattedTimestamp(DateTime timestamp)
    {
        // Normalize to UTC
        var utcTimestamp = DateTime.SpecifyKind(timestamp, DateTimeKind.Utc);

        var result = ConsolidationPromptBuilder.BuildBrainConsolidationPrompt(utcTimestamp);

        var expectedFormatted = utcTimestamp.ToString("yyyy-MM-dd HH:mm:ss");
        result.Should().Contain(expectedFormatted,
            $"prompt should contain formatted timestamp '{expectedFormatted}'");
        result.Should().NotContain("No prior consolidation",
            "prompt should not indicate 'no prior consolidation' when timestamp is provided");
    }

    /// <summary>
    /// Property 6 (null case): When lastConsolidationUtc is null, the prompt indicates no prior consolidation.
    /// **Validates: Requirements 4.3**
    /// </summary>
    [Fact]
    public void BrainConsolidationPrompt_NullTimestamp_IndicatesNoPriorConsolidation()
    {
        var result = ConsolidationPromptBuilder.BuildBrainConsolidationPrompt(lastConsolidationUtc: null);

        result.Should().Contain("No prior consolidation");
    }

    /// <summary>
    /// Property 7: Brain Consolidation Summary Includes All Metrics
    /// For any set of non-negative integer metrics (filesModified, entriesMerged,
    /// contradictionsResolved, entriesPruned), the summary string contains all four values.
    /// **Validates: Requirements 4.7**
    /// </summary>
    [Property]
    public void BrainConsolidationSummary_ContainsAllFourMetrics(
        NonNegativeInt filesModified,
        NonNegativeInt entriesMerged,
        NonNegativeInt contradictionsResolved,
        NonNegativeInt entriesPruned)
    {
        var result = ConsolidationPromptBuilder.FormatBrainConsolidationSummary(
            filesModified.Get,
            entriesMerged.Get,
            contradictionsResolved.Get,
            entriesPruned.Get);

        result.Should().Contain(filesModified.Get.ToString(),
            "summary should contain filesModified value");
        result.Should().Contain(entriesMerged.Get.ToString(),
            "summary should contain entriesMerged value");
        result.Should().Contain(contradictionsResolved.Get.ToString(),
            "summary should contain contradictionsResolved value");
        result.Should().Contain(entriesPruned.Get.ToString(),
            "summary should contain entriesPruned value");
    }
}

/// <summary>
/// FsCheck arbitrary generators for prompt builder property tests.
/// </summary>
public static class PromptBuilderArbitraries
{
    public static Arbitrary<DateTime> DateTimeArb()
    {
        return (from year in Gen.Choose(2020, 2030)
                from month in Gen.Choose(1, 12)
                from day in Gen.Choose(1, 28)
                from hour in Gen.Choose(0, 23)
                from minute in Gen.Choose(0, 59)
                from second in Gen.Choose(0, 59)
                select new DateTime(year, month, day, hour, minute, second, DateTimeKind.Utc))
            .ToArbitrary();
    }
}
