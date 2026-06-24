using Bunit;
using CodingAgentWebUI.Components.Shared;

namespace CodingAgentWebUI.UnitTests.Components;

public class CollapsibleComponentTests : BunitContext
{
    [Fact]
    public void Collapsible_Collapsed_DoesNotRenderBody()
    {
        var cut = Render<Collapsible>(p => p
            .Add(c => c.IsExpanded, false)
            .Add(c => c.CssClass, "test-section")
            .Add(c => c.HeaderClass, "test-header")
            .Add(c => c.BodyClass, "test-body")
            .Add(c => c.Header, b => b.AddContent(0, "Title"))
            .AddChildContent("Body content"));

        Assert.Contains("test-section", cut.Markup);
        Assert.Contains("Title", cut.Markup);
        Assert.DoesNotContain("Body content", cut.Markup);
    }

    [Fact]
    public void Collapsible_Expanded_RendersBody()
    {
        var cut = Render<Collapsible>(p => p
            .Add(c => c.IsExpanded, true)
            .Add(c => c.CssClass, "test-section")
            .Add(c => c.HeaderClass, "test-header")
            .Add(c => c.BodyClass, "test-body")
            .Add(c => c.Header, b => b.AddContent(0, "Title"))
            .AddChildContent("Body content"));

        Assert.Contains("Body content", cut.Markup);
    }

    [Fact]
    public void Collapsible_EmitsAriaExpanded_False_WhenCollapsed()
    {
        var cut = Render<Collapsible>(p => p
            .Add(c => c.IsExpanded, false)
            .Add(c => c.HeaderClass, "test-header")
            .Add(c => c.Header, b => b.AddContent(0, "Title")));

        var header = cut.Find(".test-header");
        Assert.Equal("false", header.GetAttribute("aria-expanded"));
    }

    [Fact]
    public void Collapsible_EmitsAriaExpanded_True_WhenExpanded()
    {
        var cut = Render<Collapsible>(p => p
            .Add(c => c.IsExpanded, true)
            .Add(c => c.HeaderClass, "test-header")
            .Add(c => c.Header, b => b.AddContent(0, "Title")));

        var header = cut.Find(".test-header");
        Assert.Equal("true", header.GetAttribute("aria-expanded"));
    }

    [Fact]
    public void Collapsible_ClickHeader_TogglesExpansion()
    {
        bool? expandedValue = null;
        var cut = Render<Collapsible>(p => p
            .Add(c => c.IsExpanded, false)
            .Add(c => c.HeaderClass, "test-header")
            .Add(c => c.Header, b => b.AddContent(0, "Title"))
            .Add(c => c.IsExpandedChanged, v => expandedValue = v)
            .AddChildContent("Body"));

        cut.Find(".test-header").Click();

        Assert.True(expandedValue);
        Assert.Contains("Body", cut.Markup);
    }

    [Fact]
    public void Collapsible_ClickExpandedHeader_Collapses()
    {
        bool? expandedValue = null;
        var cut = Render<Collapsible>(p => p
            .Add(c => c.IsExpanded, true)
            .Add(c => c.HeaderClass, "test-header")
            .Add(c => c.Header, b => b.AddContent(0, "Title"))
            .Add(c => c.IsExpandedChanged, v => expandedValue = v)
            .AddChildContent("Body"));

        cut.Find(".test-header").Click();

        Assert.False(expandedValue);
        Assert.DoesNotContain("Body", cut.Markup);
    }
}
