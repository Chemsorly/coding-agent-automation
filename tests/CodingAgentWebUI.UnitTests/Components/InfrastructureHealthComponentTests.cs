using Bunit;
using CodingAgentWebUI.Components.Pages;

namespace CodingAgentWebUI.UnitTests.Components;

public class InfrastructureHealthComponentTests : BunitContext
{
    [Fact]
    public void RendersNothing_WhenBothStatusesNull()
    {
        var cut = Render<InfrastructureHealth>(p => p
            .Add(c => c.DatabaseStatus, null)
            .Add(c => c.RedisStatus, null));

        Assert.Empty(cut.Markup.Trim());
    }

    [Fact]
    public void RendersDbIndicator_WhenDbConnected()
    {
        var cut = Render<InfrastructureHealth>(p => p
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
        var cut = Render<InfrastructureHealth>(p => p
            .Add(c => c.DatabaseStatus, false)
            .Add(c => c.RedisStatus, null));

        var dot = cut.Find(".infra-health-dot");
        Assert.Contains("dot-unhealthy", dot.ClassList.ToString());
        Assert.Contains("DB", cut.Markup);
    }

    [Fact]
    public void RendersRedisIndicator_WhenRedisConnected()
    {
        var cut = Render<InfrastructureHealth>(p => p
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
        var cut = Render<InfrastructureHealth>(p => p
            .Add(c => c.DatabaseStatus, null)
            .Add(c => c.RedisStatus, false));

        var dot = cut.Find(".infra-health-dot");
        Assert.Contains("dot-unhealthy", dot.ClassList.ToString());
        Assert.Contains("Redis", cut.Markup);
    }

    [Fact]
    public void RendersBoth_WhenBothConfigured()
    {
        var cut = Render<InfrastructureHealth>(p => p
            .Add(c => c.DatabaseStatus, true)
            .Add(c => c.RedisStatus, true));

        var dots = cut.FindAll(".infra-health-dot");
        Assert.Equal(2, dots.Count);
        Assert.Contains("DB", cut.Markup);
        Assert.Contains("Redis", cut.Markup);
    }

    [Fact]
    public void ShowsCorrectTooltip_WhenDbConnected()
    {
        var cut = Render<InfrastructureHealth>(p => p
            .Add(c => c.DatabaseStatus, true));

        var item = cut.Find(".infra-health-item");
        Assert.Equal("Database: Connected", item.GetAttribute("title"));
    }

    [Fact]
    public void ShowsCorrectTooltip_WhenDbDisconnected()
    {
        var cut = Render<InfrastructureHealth>(p => p
            .Add(c => c.DatabaseStatus, false));

        var item = cut.Find(".infra-health-item");
        Assert.Equal("Database: Disconnected", item.GetAttribute("title"));
    }

    [Fact]
    public void ShowsCorrectTooltip_WhenRedisConnected()
    {
        var cut = Render<InfrastructureHealth>(p => p
            .Add(c => c.RedisStatus, true));

        var item = cut.Find(".infra-health-item");
        Assert.Equal("Redis: Connected", item.GetAttribute("title"));
    }

    [Fact]
    public void ShowsCorrectTooltip_WhenRedisDisconnected()
    {
        var cut = Render<InfrastructureHealth>(p => p
            .Add(c => c.RedisStatus, false));

        var item = cut.Find(".infra-health-item");
        Assert.Equal("Redis: Disconnected", item.GetAttribute("title"));
    }

    [Fact]
    public void HidesDb_WhenLegacyMode()
    {
        // Legacy mode: DB not configured (null), but Redis could still be configured
        var cut = Render<InfrastructureHealth>(p => p
            .Add(c => c.DatabaseStatus, null)
            .Add(c => c.RedisStatus, true));

        Assert.DoesNotContain("DB", cut.Markup);
        Assert.Contains("Redis", cut.Markup);
    }
}
