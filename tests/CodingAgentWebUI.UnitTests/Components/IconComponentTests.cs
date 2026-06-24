using Bunit;
using CodingAgentWebUI.Components.Shared;

namespace CodingAgentWebUI.UnitTests.Components;

public class IconComponentTests : BunitContext
{
    [Fact]
    public void RendersWithCorrectDataIconAttribute()
    {
        var cut = Render<Icon>(p => p.Add(x => x.Name, "info"));

        var svg = cut.Find("svg");
        Assert.Equal("info", svg.GetAttribute("data-icon"));
    }

    [Fact]
    public void RendersWithIconCssClass()
    {
        var cut = Render<Icon>(p => p.Add(x => x.Name, "info"));

        var svg = cut.Find("svg");
        Assert.Contains("icon", svg.GetAttribute("class")!.Split(' '));
    }

    [Fact]
    public void AppliesAdditionalCssClass()
    {
        var cut = Render<Icon>(p => p
            .Add(x => x.Name, "info")
            .Add(x => x.Class, "extra"));

        var svg = cut.Find("svg");
        Assert.Contains("extra", svg.GetAttribute("class")!.Split(' '));
    }

    [Fact]
    public void AppliesSizeOverride()
    {
        var cut = Render<Icon>(p => p
            .Add(x => x.Name, "info")
            .Add(x => x.Size, "2rem"));

        var svg = cut.Find("svg");
        Assert.Contains("width:2rem", svg.GetAttribute("style")!);
        Assert.Contains("height:2rem", svg.GetAttribute("style")!);
    }

    [Fact]
    public void RendersNothingForUnknownIcon()
    {
        var cut = Render<Icon>(p => p.Add(x => x.Name, "nonexistent"));

        Assert.Empty(cut.Markup.Trim());
    }

    [Theory]
    [InlineData("message-circle")]
    [InlineData("bot")]
    [InlineData("bar-chart-2")]
    [InlineData("refresh-cw")]
    [InlineData("settings")]
    [InlineData("info")]
    public void RendersAllNavigationIcons(string iconName)
    {
        var cut = Render<Icon>(p => p.Add(x => x.Name, iconName));

        var svg = cut.Find("svg");
        Assert.Equal(iconName, svg.GetAttribute("data-icon"));
        Assert.Equal("none", svg.GetAttribute("fill"));
        Assert.Equal("currentColor", svg.GetAttribute("stroke"));
    }
}
