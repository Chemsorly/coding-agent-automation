using System.Globalization;
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

    [Property(MaxTest = 20)]
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

    [Property(MaxTest = 20)]
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
        CostFormatter.FormatCost(0.03m).Should().Be("$0.03");
    }

    [Fact]
    public void FormatCost_LargeValue_FormatsCorrectly()
    {
        CostFormatter.FormatCost(123.45m).Should().Be("$123.45");
    }

    // ── FormatTokens crash-freedom ───────────────────────────────────────

    [Property(MaxTest = 20)]
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
        CostFormatter.FormatTokens(1000).Should().Be("1.0K");
    }

    [Fact]
    public void FormatTokens_Millions_FormatsAsM()
    {
        CostFormatter.FormatTokens(2_500_000).Should().Be("2.5M");
    }

    [Property(MaxTest = 20)]
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

    [Property(MaxTest = 20)]
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
        CostFormatter.FormatBadge(5000, 1.23m).Should().Be("$1.23");
    }

    [Fact]
    public void FormatBadge_TokensFallback_WhenCostNull()
    {
        CostFormatter.FormatBadge(5000, null).Should().Be("5.0K tok");
    }

    // ── Culture invariance ───────────────────────────────────────────────

    // TODO: Add a FormatBadge culture-invariance test under non-US culture (e.g., de-DE)
    //       to directly validate that FormatBadge delegates correctly (acceptance criteria).
    //       Example: FormatBadge(2_500_000, null).Should().Be("2.5M tok") under German culture.

    [Fact]
    public void FormatTokens_GermanCulture_StillUsesDot()
    {
        var prev = Thread.CurrentThread.CurrentCulture;
        try
        {
            Thread.CurrentThread.CurrentCulture = new CultureInfo("de-DE");
            CostFormatter.FormatTokens(2_500_000).Should().Be("2.5M");
            CostFormatter.FormatTokens(12_400).Should().Be("12.4K");
            CostFormatter.FormatTokens(999).Should().Be("999");
        }
        finally
        {
            Thread.CurrentThread.CurrentCulture = prev;
        }
    }

    [Fact]
    public void FormatCost_GermanCulture_StillUsesDot()
    {
        var prev = Thread.CurrentThread.CurrentCulture;
        try
        {
            Thread.CurrentThread.CurrentCulture = new CultureInfo("de-DE");
            CostFormatter.FormatCost(1.23m).Should().Be("$1.23");
            CostFormatter.FormatCost(0.03m).Should().Be("$0.03");
        }
        finally
        {
            Thread.CurrentThread.CurrentCulture = prev;
        }
    }
}
