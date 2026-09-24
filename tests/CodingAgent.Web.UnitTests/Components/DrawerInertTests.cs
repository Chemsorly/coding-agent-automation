using Bunit;
using CodingAgent.Web.Components.Pages;
using CodingAgent.Pipeline.Models;
using CodingAgent.Pipeline.Interfaces;
using Microsoft.AspNetCore.Components;

namespace CodingAgent.Web.UnitTests.Components;

/// <summary>
/// Tests that closed dispatch drawers are inert (not focusable, not in accessibility tree).
/// When a drawer is closed, its root &lt;aside&gt; element must have the HTML "inert" attribute so
/// keyboard focus and screen-reader navigation skip its contents.
/// </summary>
public class DrawerInertTests : BunitContext
{
    // ── IssueDispatchDrawer ──────────────────────────────────────────────────

    [Fact]
    public void IssueDispatchDrawer_WhenClosed_AsideHasInertAttribute()
    {
        var cut = Render<IssueDispatchDrawer>(p => p
            .Add(c => c.IsOpen, false)
            .Add(c => c.Template, null)
            .Add(c => c.Issues, Array.Empty<IssueSummary>())
            .Add(c => c.IsLoading, false)
            .Add(c => c.IsDispatching, false)
            .Add(c => c.HasMore, false)
            .Add(c => c.Page, 1)
            .Add(c => c.IsBeingProcessed, _ => false));

        var aside = cut.Find("aside.dispatch-drawer");
        Assert.NotNull(aside.GetAttribute("inert"));
    }

    [Fact]
    public void IssueDispatchDrawer_WhenOpen_AsideDoesNotHaveInertAttribute()
    {
        var cut = Render<IssueDispatchDrawer>(p => p
            .Add(c => c.IsOpen, true)
            .Add(c => c.Template, null)
            .Add(c => c.Issues, Array.Empty<IssueSummary>())
            .Add(c => c.IsLoading, false)
            .Add(c => c.IsDispatching, false)
            .Add(c => c.HasMore, false)
            .Add(c => c.Page, 1)
            .Add(c => c.IsBeingProcessed, _ => false));

        var aside = cut.Find("aside.dispatch-drawer");
        Assert.Null(aside.GetAttribute("inert"));
    }

    [Fact]
    public void IssueDispatchDrawer_WhenClosedThenOpened_InertAttributeRemoved()
    {
        // Start closed — inert present
        var cut = Render<IssueDispatchDrawer>(p => p
            .Add(c => c.IsOpen, false)
            .Add(c => c.Template, null)
            .Add(c => c.Issues, Array.Empty<IssueSummary>())
            .Add(c => c.IsLoading, false)
            .Add(c => c.IsDispatching, false)
            .Add(c => c.HasMore, false)
            .Add(c => c.Page, 1)
            .Add(c => c.IsBeingProcessed, _ => false));

        Assert.NotNull(cut.Find("aside.dispatch-drawer").GetAttribute("inert"));

        // Transition to open on the same instance — exercises Blazor's diff/patch removal of @attributes
        cut.Render(p => p
            .Add(c => c.IsOpen, true)
            .Add(c => c.Template, null)
            .Add(c => c.Issues, Array.Empty<IssueSummary>())
            .Add(c => c.IsLoading, false)
            .Add(c => c.IsDispatching, false)
            .Add(c => c.HasMore, false)
            .Add(c => c.Page, 1)
            .Add(c => c.IsBeingProcessed, _ => false));

        Assert.Null(cut.Find("aside.dispatch-drawer").GetAttribute("inert"));
    }

    // ── PrDispatchDrawer ─────────────────────────────────────────────────────

    [Fact]
    public void PrDispatchDrawer_WhenClosed_AsideHasInertAttribute()
    {
        var cut = Render<PrDispatchDrawer>(p => p
            .Add(c => c.IsOpen, false)
            .Add(c => c.Template, null)
            .Add(c => c.PullRequests, Array.Empty<PullRequestSummary>())
            .Add(c => c.IsLoading, false)
            .Add(c => c.IsDispatching, false)
            .Add(c => c.HasMore, false)
            .Add(c => c.Page, 1)
            .Add(c => c.IsBeingProcessed, _ => false));

        var aside = cut.Find("aside.dispatch-drawer");
        Assert.NotNull(aside.GetAttribute("inert"));
    }

    [Fact]
    public void PrDispatchDrawer_WhenOpen_AsideDoesNotHaveInertAttribute()
    {
        var cut = Render<PrDispatchDrawer>(p => p
            .Add(c => c.IsOpen, true)
            .Add(c => c.Template, null)
            .Add(c => c.PullRequests, Array.Empty<PullRequestSummary>())
            .Add(c => c.IsLoading, false)
            .Add(c => c.IsDispatching, false)
            .Add(c => c.HasMore, false)
            .Add(c => c.Page, 1)
            .Add(c => c.IsBeingProcessed, _ => false));

        var aside = cut.Find("aside.dispatch-drawer");
        Assert.Null(aside.GetAttribute("inert"));
    }

    // ── EpicDispatchDrawer ───────────────────────────────────────────────────
    // TODO [WARNING]: PrDispatchDrawer and EpicDispatchDrawer are missing closed→open transition
    // tests equivalent to IssueDispatchDrawer_WhenClosedThenOpened_InertAttributeRemoved. All three
    // drawers share the same @attributes inert pattern and the same Blazor diff/patch code path.
    // Add SetParametersAndRender-based transition tests for PrDispatchDrawer and EpicDispatchDrawer
    // to ensure parity coverage.

    [Fact]
    public void EpicDispatchDrawer_WhenClosed_AsideHasInertAttribute()
    {
        var cut = Render<EpicDispatchDrawer>(p => p
            .Add(c => c.IsOpen, false)
            .Add(c => c.Template, null)
            .Add(c => c.Issues, Array.Empty<IssueSummary>())
            .Add(c => c.IsLoading, false)
            .Add(c => c.IsDispatching, false)
            .Add(c => c.HasMore, false)
            .Add(c => c.Page, 1)
            .Add(c => c.IsBeingProcessed, _ => false));

        var aside = cut.Find("aside.dispatch-drawer");
        Assert.NotNull(aside.GetAttribute("inert"));
    }

    [Fact]
    public void EpicDispatchDrawer_WhenOpen_AsideDoesNotHaveInertAttribute()
    {
        var cut = Render<EpicDispatchDrawer>(p => p
            .Add(c => c.IsOpen, true)
            .Add(c => c.Template, null)
            .Add(c => c.Issues, Array.Empty<IssueSummary>())
            .Add(c => c.IsLoading, false)
            .Add(c => c.IsDispatching, false)
            .Add(c => c.HasMore, false)
            .Add(c => c.Page, 1)
            .Add(c => c.IsBeingProcessed, _ => false));

        var aside = cut.Find("aside.dispatch-drawer");
        Assert.Null(aside.GetAttribute("inert"));
    }
}
