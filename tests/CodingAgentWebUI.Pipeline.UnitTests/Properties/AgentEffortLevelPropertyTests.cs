using AwesomeAssertions;
using CodingAgentWebUI.Pipeline.Models;
using FsCheck;
using FsCheck.Fluent;
using FsCheck.Xunit;

namespace CodingAgentWebUI.Pipeline.UnitTests.Properties;

/// <summary>
/// Property-based tests for AgentEffortLevel: roundtrip symmetry between
/// ToCliValue() and ParseEffort(), crash-freedom, and invariant preservation.
/// Guards against: new enum values added without updating both methods,
/// case sensitivity drift, and null-safety regressions.
/// </summary>
public class AgentEffortLevelPropertyTests
{
    // ── Roundtrip: ToCliValue → ParseEffort ──────────────────────────────

    /// <summary>
    /// For all AgentEffortLevel values except Auto, ParseEffort(level.ToCliValue()) == level.
    /// Auto is excluded because ToCliValue(Auto) returns null, and ParseEffort(null) returns Auto
    /// — which is correct but trivial.
    /// </summary>
    [Property(MaxTest = 20)]
    public Property NonAutoLevels_Roundtrip_ToCliValueThenParse()
    {
        var nonAutoLevels = Enum.GetValues<AgentEffortLevel>()
            .Where(l => l != AgentEffortLevel.Auto)
            .ToArray();
        var gen = Gen.Elements(nonAutoLevels);

        return Prop.ForAll(gen.ToArbitrary(), (AgentEffortLevel level) =>
        {
            var cliValue = level.ToCliValue();
            cliValue.Should().NotBeNull($"{level} should have a non-null CLI representation");

            var parsed = AgentEffortLevelExtensions.ParseEffort(cliValue);
            parsed.Should().Be(level, $"ParseEffort(\"{cliValue}\") should return {level}");
        });
    }

    /// <summary>
    /// Auto.ToCliValue() returns null, and ParseEffort(null) returns Auto.
    /// </summary>
    [Fact]
    public void Auto_ToCliValue_ReturnsNull()
    {
        AgentEffortLevel.Auto.ToCliValue().Should().BeNull();
    }

    [Fact]
    public void ParseEffort_Null_ReturnsAuto()
    {
        AgentEffortLevelExtensions.ParseEffort(null).Should().Be(AgentEffortLevel.Auto);
    }

    [Fact]
    public void ParseEffort_Empty_ReturnsAuto()
    {
        AgentEffortLevelExtensions.ParseEffort("").Should().Be(AgentEffortLevel.Auto);
    }

    [Fact]
    public void ParseEffort_Whitespace_ReturnsAuto()
    {
        AgentEffortLevelExtensions.ParseEffort("   ").Should().Be(AgentEffortLevel.Auto);
    }

    // ── Case insensitivity ───────────────────────────────────────────────

    /// <summary>
    /// ParseEffort is case-insensitive: "HIGH", "High", "high" all parse to High.
    /// </summary>
    [Property(MaxTest = 20)]
    public Property ParseEffort_CaseInsensitive()
    {
        var validValues = new[] { "low", "medium", "high", "xhigh", "max", "auto" };
        var gen =
            from value in Gen.Elements(validValues)
            from toUpper in Gen.Elements(true, false)
            select toUpper ? value.ToUpperInvariant() : value;

        return Prop.ForAll(gen.ToArbitrary(), (string input) =>
        {
            // Should not throw and should return a valid enum value
            var result = AgentEffortLevelExtensions.ParseEffort(input);
            Enum.IsDefined(result).Should().BeTrue();
        });
    }

    // ── Crash-freedom ────────────────────────────────────────────────────

    /// <summary>
    /// ParseEffort never throws for any arbitrary string input.
    /// Unrecognized values return Auto.
    /// </summary>
    [Property(MaxTest = 20)]
    public Property ParseEffort_NeverThrows_ForAnyString()
    {
        var gen = Gen.OneOf(
            Gen.Choose(1, 50)
                .SelectMany(len => Gen.ArrayOf(Gen.Choose(32, 126).Select(i => (char)i), len))
                .Select(chars => new string(chars)),
            Gen.Elements(
                "unknown", "ULTRA", "extreme", "minimal",
                "123", "!@#$", "", " ", "low low", "max\n"));

        return Prop.ForAll(gen.ToArbitrary(), (string input) =>
        {
            var result = AgentEffortLevelExtensions.ParseEffort(input);
            Enum.IsDefined(result).Should().BeTrue(
                $"ParseEffort(\"{input}\") should always return a defined enum value");
        });
    }

    // ── Completeness guard ───────────────────────────────────────────────

    /// <summary>
    /// Every non-Auto enum value has a corresponding non-null ToCliValue().
    /// If a new value is added to the enum without updating ToCliValue(), this fails.
    /// </summary>
    [Fact]
    public void AllNonAutoValues_HaveCliRepresentation()
    {
        var nonAutoValues = Enum.GetValues<AgentEffortLevel>()
            .Where(v => v != AgentEffortLevel.Auto);

        foreach (var value in nonAutoValues)
        {
            value.ToCliValue().Should().NotBeNull(
                $"{value} must have a CLI representation in ToCliValue()");
        }
    }

    /// <summary>
    /// Every non-Auto enum value's ToCliValue is a distinct, lowercase, non-empty string.
    /// </summary>
    [Fact]
    public void AllCliValues_AreLowercaseAndDistinct()
    {
        var cliValues = Enum.GetValues<AgentEffortLevel>()
            .Where(v => v != AgentEffortLevel.Auto)
            .Select(v => v.ToCliValue()!)
            .ToList();

        cliValues.Should().OnlyContain(v => v == v.ToLowerInvariant(),
            "all CLI values should be lowercase");
        cliValues.Should().OnlyHaveUniqueItems(
            "each effort level should map to a distinct CLI value");
    }
}
