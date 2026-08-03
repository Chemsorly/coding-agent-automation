using AwesomeAssertions;
using CodingAgentWebUI.Pipeline.Services;

namespace CodingAgentWebUI.Pipeline.UnitTests.Services;

/// <summary>
/// Tests for <see cref="CostFormatter"/>.
/// Verifies invariant-culture formatting for cost, token, and badge display values.
/// </summary>
public class CostFormatterTests
{
    // ── FormatCost ─────────────────────────────────────────────────────

    [Theory]
    [InlineData(0.03, "$0.03")]
    [InlineData(1.00, "$1.00")]
    [InlineData(0.001, "$0.00")]
    [InlineData(9.999, "$10.00")]
    [InlineData(100.5, "$100.50")]
    public void FormatCost_PositiveValue_FormatsWithDollarPrefix(decimal input, string expected)
    {
        CostFormatter.FormatCost(input).Should().Be(expected);
    }

    [Theory]
    [InlineData(null)]
    public void FormatCost_NullOrNonPositive_ReturnsDash(decimal? input)
    {
        CostFormatter.FormatCost(input).Should().Be("—");
    }

    [Fact]
    public void FormatCost_Zero_ReturnsDash() => CostFormatter.FormatCost(0m).Should().Be("—");

    [Fact]
    public void FormatCost_Negative_ReturnsDash() => CostFormatter.FormatCost(-1m).Should().Be("—");

    // ── FormatTokens ───────────────────────────────────────────────────

    [Theory]
    [InlineData(0, "—")]
    [InlineData(-1, "—")]
    public void FormatTokens_ZeroOrNegative_ReturnsDash(long input, string expected)
    {
        CostFormatter.FormatTokens(input).Should().Be(expected);
    }

    [Theory]
    [InlineData(1, "1")]
    [InlineData(999, "999")]
    public void FormatTokens_BelowThousand_ReturnsPlainNumber(long input, string expected)
    {
        CostFormatter.FormatTokens(input).Should().Be(expected);
    }

    [Theory]
    [InlineData(1_000, "1.0K")]
    [InlineData(1_500, "1.5K")]
    [InlineData(12_400, "12.4K")]
    [InlineData(999_999, "1000.0K")]
    public void FormatTokens_ThousandsRange_ReturnsKSuffix(long input, string expected)
    {
        CostFormatter.FormatTokens(input).Should().Be(expected);
    }

    [Theory]
    [InlineData(1_000_000, "1.0M")]
    [InlineData(1_200_000, "1.2M")]
    [InlineData(10_000_000, "10.0M")]
    public void FormatTokens_MillionsRange_ReturnsMSuffix(long input, string expected)
    {
        CostFormatter.FormatTokens(input).Should().Be(expected);
    }

    [Fact]
    public void FormatTokens_UsesInvariantCulture_DotAsDecimalSeparator()
    {
        // Explicitly verify no locale-dependent comma separator is used
        CostFormatter.FormatTokens(1_500).Should().Contain(".");
        CostFormatter.FormatTokens(1_500).Should().NotContain(",");
    }

    // ── FormatBadge ────────────────────────────────────────────────────

    [Fact]
    public void FormatBadge_HasCost_ReturnsCostNotTokens()
    {
        CostFormatter.FormatBadge(totalTokens: 50_000, totalCost: 0.05m)
            .Should().Be("$0.05");
    }

    [Fact]
    public void FormatBadge_NoCostButHasTokens_ReturnsTokensWithSuffix()
    {
        CostFormatter.FormatBadge(totalTokens: 12_400, totalCost: null)
            .Should().Be("12.4K tok");
    }

    [Fact]
    public void FormatBadge_NoCostZeroCost_FallsBackToTokens()
    {
        CostFormatter.FormatBadge(totalTokens: 5_000, totalCost: 0m)
            .Should().Be("5.0K tok");
    }

    [Fact]
    public void FormatBadge_NoCostNoTokens_ReturnsDash()
    {
        CostFormatter.FormatBadge(totalTokens: 0, totalCost: null)
            .Should().Be("—");
    }

    [Fact]
    public void FormatBadge_BothZero_ReturnsDash()
    {
        CostFormatter.FormatBadge(totalTokens: 0, totalCost: 0m)
            .Should().Be("—");
    }
}
