using AwesomeAssertions;
using CodingAgentWebUI.Pipeline.Services;
using FsCheck;
using FsCheck.Fluent;
using FsCheck.Xunit;

namespace CodingAgentWebUI.Pipeline.UnitTests.Properties;

/// <summary>
/// Property-based tests for CostFormatter: verifies crash-freedom and output format
/// invariants across all valid inputs. CostFormatter had zero prior test coverage —
/// these tests guard against division-by-zero, overflow, and format edge cases
/// that could crash the Blazor render cycle.
/// </summary>
public class CostFormatterPropertyTests
{
    // ── FormatCost crash-freedom ──────────────────────────────────────────

    [Property(MaxTest = 100)]
    public Property FormatCost_NeverThrows_ForAnyDecimal()
    {
        var gen = Gen.Choose(-1_000_000, 1_000_000).Select(i => (decimal)i / 100);
        return Prop.ForAll(gen.ToArbitrary(), value =>
        {
            var result = CostFormatter.FormatCost(value);
            result.Should().NotBeNull();
        });
    }

    [Fact]
    public void FormatCost_Null_ReturnsDash()
    {
        CostFormatter.FormatCost(null).Should().Be("\u2014");
    }

    [Fact]
    public void FormatCost_Zero_ReturnsDash()
    {
        CostFormatter.FormatCost(0m).Should().Be("\u2014");
    }

    [Fact]
    public void FormatCost_Negative_ReturnsDash()
    {
        CostFormatter.FormatCost(-5.50m).Should().Be("\u2014");
    }

    [Property(MaxTest = 50)]
    public Property FormatCost_Positive_StartsWithDollarSign()
    {
        var gen = Gen.Choose(1, 999_999).Select(i => (decimal)i / 100);
        return Prop.ForAll(gen.ToArbitrary(), cost =>
        {
            var result = CostFormatter.FormatCost(cost);
            result.Should().StartWith("$");
        });
    }

    [Fact]
    public void FormatCost_MaxDecimal_DoesNotThrow()
    {
        var result = CostFormatter.FormatCost(decimal.MaxValue);
        result.Should().StartWith("$");
        result.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public void FormatCost_SmallPositive_FormatsCorrectly()
    {
        var result = CostFormatter.FormatCost(0.03m);
        result.Should().StartWith("$");
        result.Should().Contain("0");
        result.Should().Contain("03");
    }

    [Fact]
    public void FormatCost_LargeValue_FormatsCorrectly()
    {
        var result = CostFormatter.FormatCost(123.45m);
        result.Should().StartWith("$");
        result.Should().Contain("123");
        result.Should().Contain("45");
    }

    // ── FormatTokens crash-freedom ───────────────────────────────────────

    [Property(MaxTest = 100)]
    public Property FormatTokens_NeverThrows_ForAnyLong()
    {
        var gen = Gen.Choose(int.MinValue, int.MaxValue).Select(i => (long)i);
        return Prop.ForAll(gen.ToArbitrary(), value =>
        {
            var result = CostFormatter.FormatTokens(value);
            result.Should().NotBeNullOrEmpty();
        });
    }

    [Fact]
    public void FormatTokens_Zero_ReturnsDash()
    {
        CostFormatter.FormatTokens(0).Should().Be("\u2014");
    }

    [Fact]
    public void FormatTokens_Negative_ReturnsDash()
    {
        CostFormatter.FormatTokens(-100).Should().Be("\u2014");
    }

    [Fact]
    public void FormatTokens_LongMinValue_ReturnsDash()
    {
        CostFormatter.FormatTokens(long.MinValue).Should().Be("\u2014");
    }

    [Fact]
    public void FormatTokens_LongMaxValue_DoesNotThrow()
    {
        var result = CostFormatter.FormatTokens(long.MaxValue);
        result.Should().NotBeNullOrEmpty();
        result.Should().EndWith("M");
    }

    [Fact]
    public void FormatTokens_Under1000_ReturnsRawNumber()
    {
        CostFormatter.FormatTokens(999).Should().Be("999");
    }

    [Fact]
    public void FormatTokens_Exactly1000_FormatsAsK()
    {
        var result = CostFormatter.FormatTokens(1000);
        result.Should().EndWith("K");
        result.Should().Contain("1");
    }

    [Fact]
    public void FormatTokens_Millions_FormatsAsM()
    {
        var result = CostFormatter.FormatTokens(2_500_000);
        result.Should().EndWith("M");
        result.Should().Contain("2");
    }

    [Property(MaxTest = 50)]
    public Property FormatTokens_Positive_NeverReturnsDash()
    {
        var gen = Gen.Choose(1, int.MaxValue).Select(i => (long)i);
        return Prop.ForAll(gen.ToArbitrary(), tokens =>
        {
            var result = CostFormatter.FormatTokens(tokens);
            result.Should().NotBe("\u2014");
        });
    }

    // ── FormatBadge crash-freedom ────────────────────────────────────────

    [Property(MaxTest = 100)]
    public Property FormatBadge_NeverThrows_ForAnyInput()
    {
        var gen =
            from tokens in Gen.Choose(-1000, 10_000_000).Select(i => (long)i)
            from costRaw in Gen.Choose(-100, 10000).Select(i => (decimal)i / 100)
            from isNull in Gen.Elements(true, false)
            select (tokens, isNull ? (decimal?)null : costRaw);

        return Prop.ForAll(gen.ToArbitrary(), tuple =>
        {
            var (tokens, cost) = tuple;
            var result = CostFormatter.FormatBadge(tokens, cost);
            result.Should().NotBeNullOrEmpty();
        });
    }

    [Fact]
    public void FormatBadge_BothZero_ReturnsDash()
    {
        CostFormatter.FormatBadge(0, null).Should().Be("\u2014");
    }

    [Fact]
    public void FormatBadge_CostPreferred_WhenBothPositive()
    {
        var result = CostFormatter.FormatBadge(5000, 1.23m);
        result.Should().StartWith("$");
    }

    [Fact]
    public void FormatBadge_TokensFallback_WhenCostNull()
    {
        var result = CostFormatter.FormatBadge(5000, null);
        result.Should().Contain("tok");
    }
}
