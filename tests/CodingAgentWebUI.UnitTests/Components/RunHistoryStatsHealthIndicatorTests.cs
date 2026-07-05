using Bunit;
using CodingAgentWebUI.Components.Pages;
using CodingAgentWebUI.Pipeline.Models;

namespace CodingAgentWebUI.UnitTests.Components;

public class RunHistoryStatsHealthIndicatorTests : BunitContext
{
    [Fact]
    public void RendersNoHealthIndicators_WhenBothStatusesNull()
    {
        var cut = Render<RunHistoryStats>(p => p
            .Add(c => c.Runs, Array.Empty<PipelineRunSummary>())
            .Add(c => c.DatabaseStatus, null)
            .Add(c => c.RedisStatus, null));

        Assert.DoesNotContain("infra-health-dot", cut.Markup);
        Assert.DoesNotContain("DB", cut.Markup);
        Assert.DoesNotContain("Redis", cut.Markup);
    }

    [Fact]
    public void RendersDbIndicator_WhenDbConnected()
    {
        var cut = Render<RunHistoryStats>(p => p
            .Add(c => c.Runs, Array.Empty<PipelineRunSummary>())
            .Add(c => c.DatabaseStatus, true)
            .Add(c => c.RedisStatus, null));

        var dot = cut.Find(".infra-health-dot");
        Assert.Contains("dot-healthy", dot.ClassList.ToString());
        Assert.Contains("DB", cut.Markup);
        Assert.DoesNotContain("Redis", cut.Markup);
    }

    [Fact]
    public void RendersDbIndicator_WhenDbDisconnected()
    {
        var cut = Render<RunHistoryStats>(p => p
            .Add(c => c.Runs, Array.Empty<PipelineRunSummary>())
            .Add(c => c.DatabaseStatus, false)
            .Add(c => c.RedisStatus, null));

        var dot = cut.Find(".infra-health-dot");
        Assert.Contains("dot-unhealthy", dot.ClassList.ToString());
        Assert.Contains("DB", cut.Markup);
    }

    [Fact]
    public void RendersRedisIndicator_WhenRedisConnected()
    {
        var cut = Render<RunHistoryStats>(p => p
            .Add(c => c.Runs, Array.Empty<PipelineRunSummary>())
            .Add(c => c.DatabaseStatus, null)
            .Add(c => c.RedisStatus, true));

        var dot = cut.Find(".infra-health-dot");
        Assert.Contains("dot-healthy", dot.ClassList.ToString());
        Assert.Contains("Redis", cut.Markup);
        Assert.DoesNotContain("DB", cut.Markup);
    }

    [Fact]
    public void RendersRedisIndicator_WhenRedisDisconnected()
    {
        var cut = Render<RunHistoryStats>(p => p
            .Add(c => c.Runs, Array.Empty<PipelineRunSummary>())
            .Add(c => c.DatabaseStatus, null)
            .Add(c => c.RedisStatus, false));

        var dot = cut.Find(".infra-health-dot");
        Assert.Contains("dot-unhealthy", dot.ClassList.ToString());
        Assert.Contains("Redis", cut.Markup);
    }

    [Fact]
    public void RendersBothIndicators_WhenBothConfigured()
    {
        var cut = Render<RunHistoryStats>(p => p
            .Add(c => c.Runs, Array.Empty<PipelineRunSummary>())
            .Add(c => c.DatabaseStatus, true)
            .Add(c => c.RedisStatus, true));

        var dots = cut.FindAll(".infra-health-dot");
        Assert.Equal(2, dots.Count);
        Assert.Contains("DB", cut.Markup);
        Assert.Contains("Redis", cut.Markup);
        // TODO: Assert ordering of .infra-health-item elements (DB before Redis) to catch accidental rendering order swaps
    }

    [Fact]
    public void ShowsCorrectTooltip_WhenDbConnected()
    {
        var cut = Render<RunHistoryStats>(p => p
            .Add(c => c.Runs, Array.Empty<PipelineRunSummary>())
            .Add(c => c.DatabaseStatus, true));

        var item = cut.Find(".infra-health-item");
        Assert.Equal("Database: Connected", item.GetAttribute("title"));
    }

    [Fact]
    public void ShowsCorrectTooltip_WhenDbDisconnected()
    {
        var cut = Render<RunHistoryStats>(p => p
            .Add(c => c.Runs, Array.Empty<PipelineRunSummary>())
            .Add(c => c.DatabaseStatus, false));

        var item = cut.Find(".infra-health-item");
        Assert.Equal("Database: Disconnected", item.GetAttribute("title"));
    }

    [Fact]
    public void ShowsCorrectTooltip_WhenRedisConnected()
    {
        var cut = Render<RunHistoryStats>(p => p
            .Add(c => c.Runs, Array.Empty<PipelineRunSummary>())
            .Add(c => c.RedisStatus, true));

        var item = cut.Find(".infra-health-item");
        Assert.Equal("Redis: Connected", item.GetAttribute("title"));
    }

    [Fact]
    public void ShowsCorrectTooltip_WhenRedisDisconnected()
    {
        var cut = Render<RunHistoryStats>(p => p
            .Add(c => c.Runs, Array.Empty<PipelineRunSummary>())
            .Add(c => c.RedisStatus, false));

        var item = cut.Find(".infra-health-item");
        Assert.Equal("Redis: Disconnected", item.GetAttribute("title"));
    }

    [Fact]
    public void RendersDivider_WhenHealthIndicatorsPresent()
    {
        var cut = Render<RunHistoryStats>(p => p
            .Add(c => c.Runs, Array.Empty<PipelineRunSummary>())
            .Add(c => c.DatabaseStatus, true));

        var dividers = cut.FindAll(".run-stats-divider");
        // TODO: Use Assert.Equal(1, dividers.Count) to verify exactly one health-indicator divider is rendered
        Assert.True(dividers.Count > 0);
    }

    [Fact]
    public void NoDividerOrIndicators_WhenBothNull()
    {
        var cut = Render<RunHistoryStats>(p => p
            .Add(c => c.Runs, Array.Empty<PipelineRunSummary>())
            .Add(c => c.DatabaseStatus, null)
            .Add(c => c.RedisStatus, null));

        Assert.DoesNotContain("infra-health-item", cut.Markup);
        // No dividers when no stats and no health indicators
        var dividers = cut.FindAll(".run-stats-divider");
        Assert.Empty(dividers);
    }

    [Fact]
    public void HidesDb_WhenLegacyMode()
    {
        // Legacy mode: DB not configured (null), but Redis could still be configured
        var cut = Render<RunHistoryStats>(p => p
            .Add(c => c.Runs, Array.Empty<PipelineRunSummary>())
            .Add(c => c.DatabaseStatus, null)
            .Add(c => c.RedisStatus, true));

        Assert.DoesNotContain("DB", cut.Markup);
        Assert.Contains("Redis", cut.Markup);
    }
}
