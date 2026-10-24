using AwesomeAssertions;
using Bunit;
using CodingAgent.Pipeline.Models;
using CodingAgent.Web.Components.Shared;

namespace CodingAgent.Web.UnitTests.Components;

/// <summary>Unit tests for <see cref="UsageText"/> formatting methods and <see cref="UsageCost"/> component.</summary>
public class UsageTextTests : BunitContext
{
    // ── Headline ──────────────────────────────────────────────────────────────

    [Fact]
    public void Headline_NullUsage_ReturnsDash()
    {
        UsageText.Headline(null).Should().Be("—");
    }

    [Fact]
    public void Headline_ZeroCostAndZeroTokens_ReturnsDash()
    {
        var usage = new RunUsage { Cost = 0m, Tokens = 0 };
        UsageText.Headline(usage).Should().Be("—");
    }

    [Fact]
    public void Headline_SubscriptionCost_ReturnsApprox()
    {
        var usage = new RunUsage { Cost = 17.36m, BillingMode = AgentBillingModes.Subscription };
        UsageText.Headline(usage).Should().Be("≈ $17.36");
    }

    [Fact]
    public void Headline_ApiCost_ReturnsDollar()
    {
        var usage = new RunUsage { Cost = 17.36m, BillingMode = AgentBillingModes.Api };
        UsageText.Headline(usage).Should().Be("$17.36");
    }

    [Fact]
    public void Headline_TokensOnlyNoCost_ReturnsTokens()
    {
        var usage = new RunUsage { Tokens = 229_452, Cost = null };
        UsageText.Headline(usage).Should().Be("229.5K tokens");
    }

    // ── Tooltip ───────────────────────────────────────────────────────────────

    [Fact]
    public void Tooltip_ApiRun_ListsAllLines()
    {
        var usage = new RunUsage
        {
            Cost = 17.36m,
            Tokens = 229_452,
            InputTokens = 452,
            OutputTokens = 124_641,
            ReasoningTokens = 104_359,
            CacheReadTokens = 32_180_429,
            CacheWriteTokens = 1_269_310,
            BillingMode = AgentBillingModes.Api,
            Model = "claude-opus-5-5",
        };

        var expected = "$17.36 · API billing · claude-opus-5-5\n" +
                       "New tokens 229.5K (input 452 · output 124.6K · thinking 104.4K)\n" +
                       "Cache read 32.2M\n" +
                       "Cache write 1.3M";

        UsageText.Tooltip(usage).Should().Be(expected);
    }

    [Fact]
    public void Tooltip_SubscriptionRun_MarksTheEstimate()
    {
        var usage = new RunUsage { Cost = 12.50m, BillingMode = AgentBillingModes.Subscription, Tokens = 50_000 };
        var tooltip = UsageText.Tooltip(usage);
        tooltip.Should().StartWith("≈ $12.50 · subscription (API-price estimate, not billed)");
    }

    [Fact]
    public void Tooltip_TokensOnly_SaysNoCostReported()
    {
        var usage = new RunUsage { Tokens = 5_000, Cost = null };
        var tooltip = UsageText.Tooltip(usage);
        tooltip.Should().StartWith("No cost reported");
        tooltip.Should().Contain("New tokens 5.0K");
    }

    [Fact]
    public void Tooltip_UnknownSplit_OmitsTheParenthesis()
    {
        // InputTokens + OutputTokens + ReasoningTokens = 0, so no breakdown parenthetical.
        var usage = new RunUsage { Tokens = 5_000, Cost = 0.03m, BillingMode = AgentBillingModes.Api };
        var tooltip = UsageText.Tooltip(usage);
        tooltip.Should().Contain("New tokens 5.0K");
        tooltip.Should().NotContain("(input");
    }

    // ── BreakdownLine ─────────────────────────────────────────────────────────

    [Fact]
    public void BreakdownLine_JoinsLinesTwoToFour()
    {
        var usage = new RunUsage
        {
            Tokens = 229_452,
            InputTokens = 452,
            OutputTokens = 124_641,
            ReasoningTokens = 104_359,
            CacheReadTokens = 32_180_429,
            CacheWriteTokens = 1_269_310,
        };

        var line = UsageText.BreakdownLine(usage);
        line.Should().Be(
            "New tokens 229.5K (input 452 · output 124.6K · thinking 104.4K) · " +
            "Cache read 32.2M · " +
            "Cache write 1.3M");
    }

    // ── UsageCost component ───────────────────────────────────────────────────

    [Fact]
    public void UsageCost_SubscriptionBillingWithCost_HasEstimateClass()
    {
        var usage = new RunUsage { Cost = 12.50m, BillingMode = AgentBillingModes.Subscription, Tokens = 50_000 };

        var cut = Render<UsageCost>(p => p.Add(c => c.Usage, usage));

        var span = cut.Find("[data-testid='usage-cost']");
        span.ClassList.Should().Contain("usage-cost-estimate");
    }
}
