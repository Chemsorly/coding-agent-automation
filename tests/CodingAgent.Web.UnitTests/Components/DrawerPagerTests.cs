using Bunit;
using CodingAgent.Web.Components.Pages;

namespace CodingAgent.Web.UnitTests.Components;

public class DrawerPagerTests : BunitContext
{
    [Fact]
    public void FirstPageWithMore_DisablesPrevAndEnablesNext()
    {
        var cut = Render<DrawerPager>(p => p
            .Add(c => c.Page, 1)
            .Add(c => c.HasMore, true));

        var buttons = cut.FindAll("button.btn-page");
        Assert.Equal(2, buttons.Count);

        var prev = buttons.First(b => b.TextContent.Contains("Prev"));
        var next = buttons.First(b => b.TextContent.Contains("Next"));

        Assert.NotNull(prev.GetAttribute("disabled"));
        Assert.Null(next.GetAttribute("disabled"));
    }

    [Fact]
    public void LastPage_DisablesNext()
    {
        var cut = Render<DrawerPager>(p => p
            .Add(c => c.Page, 2)
            .Add(c => c.HasMore, false));

        var buttons = cut.FindAll("button.btn-page");
        Assert.Equal(2, buttons.Count);

        var prev = buttons.First(b => b.TextContent.Contains("Prev"));
        var next = buttons.First(b => b.TextContent.Contains("Next"));

        Assert.Null(prev.GetAttribute("disabled"));
        Assert.NotNull(next.GetAttribute("disabled"));
    }

    [Fact]
    public void FirstPageWithoutMore_RendersNothing()
    {
        var cut = Render<DrawerPager>(p => p
            .Add(c => c.Page, 1)
            .Add(c => c.HasMore, false));

        Assert.Empty(cut.FindAll("button.btn-page"));
    }
    // TODO: No test verifies that clicking the Next button invokes OnNextPage or that clicking the Prev button
    // invokes OnPrevPage. If the two callbacks were swapped in DrawerPager.razor (@onclick="OnNextPage" on the
    // Prev button and vice versa), all existing tests would still pass because they only check disabled state,
    // not callback routing. Add NextButton_Click_InvokesOnNextPage and PrevButton_Click_InvokesOnPrevPage tests.
}
