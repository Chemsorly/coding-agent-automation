using Bunit;
using Moq;
using Microsoft.AspNetCore.Components;
using CodingAgent.Pipeline.Models;
using CodingAgent.Web.Components.Pages;

namespace CodingAgent.Web.UnitTests.Components;

/// <summary>
/// Tests for label-filter chip rendering in the three dispatch drawers.
///
/// The chips are &lt;button class="sidebar-label …"&gt; elements. Buttons do not
/// inherit `color` from their parent (browser default for form controls), so they
/// need an explicit color rule — added to cockpit.css as
/// `.cockpit button.sidebar-label { color: var(--text); }` in issue #2938.
///
/// These tests guard the markup contract: if chips are ever changed to a different
/// element type, or if the sidebar-label class is removed, the CSS fix would stop
/// applying. They also verify that AvailableLabels wired from the parent actually
/// produces rendered button chips.
/// </summary>
public class DispatchDrawerLabelTests : BunitContext
{
    private static readonly string[] TestLabels = { "bug", "documentation", "agent:done" };

    private static PipelineJobTemplate MakeTemplate() =>
        new() { Id = "t-1", Name = "Template", IssueProviderId = "ip-1", RepoProviderId = "rp-1" };

    // ── IssueDispatchDrawer ──────────────────────────────────────────────────

    [Fact]
    public void IssueDrawer_LabelFilterChips_RenderAsButtonsWithSidebarLabelClass()
    {
        var cut = Render<IssueDispatchDrawer>(p => p
            .Add(s => s.IsOpen, true)
            .Add(s => s.Template, MakeTemplate())
            .Add(s => s.Issues, new List<IssueSummary>())
            .Add(s => s.IsLoading, false)
            .Add(s => s.IsDispatching, false)
            .Add(s => s.IsBeingProcessed, _ => false)
            .Add(s => s.HasMore, false)
            .Add(s => s.Page, 1)
            .Add(s => s.AvailableLabels, TestLabels)
            .Add(s => s.SelectedLabels, Array.Empty<string>()));

        var chips = cut.FindAll("button.sidebar-label");
        Assert.Equal(TestLabels.Length, chips.Count);

        // TODO: The per-chip class assertion below is tautological — FindAll("button.sidebar-label")
        // already guarantees each result has sidebar-label. Consider replacing with an assertion
        // that verifies each chip's text content matches the corresponding TestLabels entry. (review finding #2938)
        foreach (var chip in chips)
        {
            Assert.Contains("sidebar-label", chip.GetAttribute("class") ?? "");
        }
    }

    [Fact]
    public void IssueDrawer_LabelFilterChips_AreNotSpans()
    {
        // TODO: This test is a redundant negative of IssueDrawer_LabelFilterChips_RenderAsButtonsWithSidebarLabelClass.
        // If chips were changed from <button> to <span>, the positive test would already fail.
        // Consider replacing with a test that verifies label text content or selected-state (label-active class). (review finding #2938)
        var cut = Render<IssueDispatchDrawer>(p => p
            .Add(s => s.IsOpen, true)
            .Add(s => s.Template, MakeTemplate())
            .Add(s => s.Issues, new List<IssueSummary>())
            .Add(s => s.IsLoading, false)
            .Add(s => s.IsDispatching, false)
            .Add(s => s.IsBeingProcessed, _ => false)
            .Add(s => s.HasMore, false)
            .Add(s => s.Page, 1)
            .Add(s => s.AvailableLabels, TestLabels)
            .Add(s => s.SelectedLabels, Array.Empty<string>()));

        // Filter chips must be <button>, not <span>, so the button.sidebar-label CSS rule applies
        var drawerFilter = cut.Find(".drawer-label-filter");
        var spanChips = drawerFilter.QuerySelectorAll("span.sidebar-label");
        // There should be no span-based filter chips (spans are for card labels, not filter chips)
        Assert.Empty(spanChips);
    }

    // ── PrDispatchDrawer ─────────────────────────────────────────────────────

    [Fact]
    public void PrDrawer_LabelFilterChips_RenderAsButtonsWithSidebarLabelClass()
    {
        var cut = Render<PrDispatchDrawer>(p => p
            .Add(s => s.IsOpen, true)
            .Add(s => s.Template, MakeTemplate())
            .Add(s => s.PullRequests, new List<PullRequestSummary>())
            .Add(s => s.IsLoading, false)
            .Add(s => s.IsDispatching, false)
            .Add(s => s.IsBeingProcessed, _ => false)
            .Add(s => s.HasMore, false)
            .Add(s => s.Page, 1)
            .Add(s => s.AvailableLabels, TestLabels)
            .Add(s => s.SelectedLabels, Array.Empty<string>()));

        var chips = cut.FindAll("button.sidebar-label");
        Assert.Equal(TestLabels.Length, chips.Count);

        // TODO: The per-chip class assertion below is tautological — FindAll("button.sidebar-label")
        // already guarantees each result has sidebar-label. Consider replacing with an assertion
        // that verifies each chip's text content matches the corresponding TestLabels entry. (review finding #2938)
        foreach (var chip in chips)
        {
            Assert.Contains("sidebar-label", chip.GetAttribute("class") ?? "");
        }
    }

    // ── EpicDispatchDrawer ───────────────────────────────────────────────────

    [Fact]
    public void EpicDrawer_LabelFilterChips_RenderAsButtonsWithSidebarLabelClass()
    {
        var cut = Render<EpicDispatchDrawer>(p => p
            .Add(s => s.IsOpen, true)
            .Add(s => s.Template, MakeTemplate())
            .Add(s => s.Issues, new List<IssueSummary>())
            .Add(s => s.IsLoading, false)
            .Add(s => s.IsDispatching, false)
            .Add(s => s.IsBeingProcessed, _ => false)
            .Add(s => s.HasMore, false)
            .Add(s => s.Page, 1)
            .Add(s => s.AvailableLabels, TestLabels)
            .Add(s => s.SelectedLabels, Array.Empty<string>()));

        var chips = cut.FindAll("button.sidebar-label");
        Assert.Equal(TestLabels.Length, chips.Count);

        // TODO: The per-chip class assertion below is tautological — FindAll("button.sidebar-label")
        // already guarantees each result has sidebar-label. Consider replacing with an assertion
        // that verifies each chip's text content matches the corresponding TestLabels entry. (review finding #2938)
        // TODO: No test covers the empty-label edge case (AvailableLabels = empty/null) — add a test
        // to verify the filter section does not render when no labels are available. (review finding #2938)
        // TODO: No test covers the selected-state (label-active class) — add a test passing
        // SelectedLabels = new[] { "bug" } and asserting the chip carries label-active. (review finding #2938)
        foreach (var chip in chips)
        {
            Assert.Contains("sidebar-label", chip.GetAttribute("class") ?? "");
        }
    }
}
