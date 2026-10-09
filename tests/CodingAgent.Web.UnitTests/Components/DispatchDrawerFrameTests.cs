using Bunit;
using CodingAgent.Web.Components.Pages;
using Microsoft.AspNetCore.Components;

namespace CodingAgent.Web.UnitTests.Components;

public class DispatchDrawerFrameTests : BunitContext
{
    [Fact]
    public void Closed_AsideIsInert()
    {
        var cut = Render<DispatchDrawerFrame>(p => p
            .Add(c => c.IsOpen, false)
            .Add(c => c.Title, "Test"));

        var aside = cut.Find("aside.dispatch-drawer");
        Assert.NotNull(aside.GetAttribute("inert"));
    }

    [Fact]
    public void Open_AsideIsNotInert()
    {
        var cut = Render<DispatchDrawerFrame>(p => p
            .Add(c => c.IsOpen, true)
            .Add(c => c.Title, "Test"));

        var aside = cut.Find("aside.dispatch-drawer");
        Assert.Null(aside.GetAttribute("inert"));
    }

    [Fact]
    public void CloseButton_InvokesOnClose()
    {
        var closeCalled = false;
        var cut = Render<DispatchDrawerFrame>(p => p
            .Add(c => c.IsOpen, true)
            .Add(c => c.Title, "Test")
            .Add(c => c.OnClose, EventCallback.Factory.Create(this, () => closeCalled = true)));

        cut.Find("button.agent-detail-close").Click();

        Assert.True(closeCalled);
    }

    [Fact]
    public void OverlayClick_InvokesOnClose()
    {
        var closeCalled = false;
        var cut = Render<DispatchDrawerFrame>(p => p
            .Add(c => c.IsOpen, true)
            .Add(c => c.Title, "Test")
            .Add(c => c.OnClose, EventCallback.Factory.Create(this, () => closeCalled = true)));

        cut.Find(".dispatch-drawer-overlay").Click();

        Assert.True(closeCalled);
    }

    [Fact]
    public void RendersTitleHeaderPrefixAndChildContent()
    {
        var cut = Render<DispatchDrawerFrame>(p => p
            .Add(c => c.IsOpen, true)
            .Add(c => c.Title, "Test Title")
            .Add(c => c.HeaderPrefix, builder =>
            {
                builder.OpenElement(0, "span");
                builder.AddAttribute(1, "id", "header-prefix-sentinel");
                builder.AddContent(2, "prefix-content");
                builder.CloseElement();
            })
            .Add(c => c.ChildContent, builder =>
            {
                builder.OpenElement(0, "div");
                builder.AddAttribute(1, "id", "child-content-sentinel");
                builder.AddContent(2, "child-content");
                builder.CloseElement();
            }));

        Assert.Equal("Test Title", cut.Find("h3").TextContent.Trim());
        // TODO: Assert.NotNull here is tautological — bUnit's Find() throws ElementNotFoundException if the element
        // is absent and never returns null, so the Assert.NotNull adds no detection value. The presence assertion
        // is carried entirely by the Find() call. Consider removing the Assert.NotNull wrappers.
        Assert.NotNull(cut.Find("#header-prefix-sentinel"));
        Assert.NotNull(cut.Find("#child-content-sentinel"));
    }
}
