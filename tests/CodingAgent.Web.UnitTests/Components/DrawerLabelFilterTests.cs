using Bunit;
using CodingAgent.Web.Components.Pages;
using Microsoft.AspNetCore.Components;

namespace CodingAgent.Web.UnitTests.Components;

public class DrawerLabelFilterTests : BunitContext
{
    [Fact]
    public void NoAvailableLabels_RendersNothing()
    {
        var cut = Render<DrawerLabelFilter>(p => p
            .Add(c => c.AvailableLabels, Array.Empty<string>()));

        Assert.Empty(cut.FindAll(".drawer-label-filter"));
    }

    [Fact]
    public void NoSelectedLabels_HidesClearButton()
    {
        var cut = Render<DrawerLabelFilter>(p => p
            .Add(c => c.AvailableLabels, new[] { "bug" })
            .Add(c => c.SelectedLabels, Array.Empty<string>()));

        var buttons = cut.FindAll("button");
        Assert.DoesNotContain(buttons, b => b.TextContent.Trim() == "Clear");
    }

    [Fact]
    public void SelectedLabel_ShowsClearButton()
    {
        var cut = Render<DrawerLabelFilter>(p => p
            .Add(c => c.AvailableLabels, new[] { "bug" })
            .Add(c => c.SelectedLabels, new[] { "bug" }));

        var buttons = cut.FindAll("button");
        Assert.Contains(buttons, b => b.TextContent.Trim() == "Clear");
    }

    [Fact]
    public void LabelClick_InvokesOnToggleLabelWithThatLabel()
    {
        string? invokedWith = null;
        var cut = Render<DrawerLabelFilter>(p => p
            .Add(c => c.AvailableLabels, new[] { "bug", "feature" })
            .Add(c => c.SelectedLabels, Array.Empty<string>())
            .Add(c => c.OnToggleLabel, EventCallback.Factory.Create<string>(this, s => invokedWith = s)));

        // Find the "bug" label button (not the Clear button) and click it
        var labelBtns = cut.FindAll("button").Where(b => b.TextContent.Trim() == "bug").ToList();
        Assert.Single(labelBtns);
        labelBtns[0].Click();

        Assert.Equal("bug", invokedWith);
    }
    // TODO: No test verifies that clicking the Clear button invokes OnClearLabels. The component wires
    // @onclick="OnClearLabels" directly on the Clear button; an accidental removal or mis-binding of that
    // handler would be undetected by the existing suite which only asserts the button's presence/absence.
    // Add a ClearClick_InvokesOnClearLabels test.
}
