using Bunit;
using CodingAgent.Web.Components.Pages;
using CodingAgent.Pipeline.Models;
using CodingAgent.Pipeline.Interfaces;

namespace CodingAgent.Web.UnitTests.Components;

/// <summary>
/// Characterization tests that assert the full structural markup of each dispatch drawer.
/// These tests pass against the current drawers and serve as regression guards through and
/// after the component-extraction refactor (issue #3453).
/// </summary>
public class DispatchDrawerMarkupTests : BunitContext
{
    private static readonly PipelineJobTemplate Template = new()
    {
        Id = "t", Name = "T", IssueProviderId = "i", RepoProviderId = "r"
    };

    [Fact]
    public void EpicDispatchDrawer_RendersFrameLabelFilterAndPager()
    {
        IssueSummary Issue(string id) => new()
        {
            Identifier = id, Title = $"Epic {id}", Labels = [], Description = ""
        };
        var cut = Render<EpicDispatchDrawer>(p => p
            .Add(c => c.IsOpen, true)
            .Add(c => c.Template, Template)
            .Add(c => c.Issues, new[] { Issue("1"), Issue("2") })
            .Add(c => c.IsLoading, false)
            .Add(c => c.IsDispatching, false)
            .Add(c => c.HasMore, true)
            .Add(c => c.Page, 2)
            .Add(c => c.AvailableLabels, new[] { "bug", "feature" })
            .Add(c => c.SelectedLabels, new[] { "bug" })
            .Add(c => c.GetProcessingStatus, _ => null));

        // Open aside without inert
        var aside = cut.Find("aside.dispatch-drawer.open");
        Assert.Null(aside.GetAttribute("inert"));

        // Open overlay
        cut.Find(".dispatch-drawer-overlay.open");

        // Title in h3
        Assert.Equal("Select Epic for Decomposition", cut.Find("h3").TextContent.Trim());

        // Close button aria-label
        var closeBtn = cut.Find("button.agent-detail-close");
        Assert.Equal("Close drawer", closeBtn.GetAttribute("aria-label"));

        // Label filter: bug, feature, Clear
        var labelFilter = cut.Find(".drawer-label-filter");
        Assert.NotNull(labelFilter);
        var labelBtns = labelFilter.QuerySelectorAll("button");
        Assert.Contains(labelBtns, b => b.TextContent.Trim() == "bug");
        Assert.Contains(labelBtns, b => b.TextContent.Trim() == "feature");
        Assert.Contains(labelBtns, b => b.TextContent.Trim() == "Clear");

        // Two enabled btn-page buttons and Page 2 text
        var pageBtns = cut.FindAll("button.btn-page");
        Assert.Equal(2, pageBtns.Count);
        Assert.All(pageBtns, b => Assert.Null(b.GetAttribute("disabled")));
        Assert.Contains("Page 2", cut.Markup);

        // Row data-testid values
        cut.Find("[data-testid='epic-row-1']");
        cut.Find("[data-testid='epic-row-2']");

        // Dispatch button (requires selecting a row first)
        cut.Find("[data-testid='epic-row-1']").Click();
        cut.Find("[data-testid='dispatch-epic-btn']");
    }

    [Fact]
    public void IssueDispatchDrawer_RendersFrameLabelFilterAndPager()
    {
        IssueSummary Issue(string id) => new()
        {
            Identifier = id, Title = $"Issue {id}", Labels = [], Description = ""
        };
        var cut = Render<IssueDispatchDrawer>(p => p
            .Add(c => c.IsOpen, true)
            .Add(c => c.Template, Template)
            .Add(c => c.Issues, new[] { Issue("1"), Issue("2") })
            .Add(c => c.IsLoading, false)
            .Add(c => c.IsDispatching, false)
            .Add(c => c.HasMore, true)
            .Add(c => c.Page, 2)
            .Add(c => c.AvailableLabels, new[] { "bug", "feature" })
            .Add(c => c.SelectedLabels, new[] { "bug" })
            .Add(c => c.GetProcessingStatus, _ => null));

        // Open aside without inert
        var aside = cut.Find("aside.dispatch-drawer.open");
        Assert.Null(aside.GetAttribute("inert"));

        // Open overlay
        cut.Find(".dispatch-drawer-overlay.open");

        // Title in h3
        Assert.Equal("Browse Issues", cut.Find("h3").TextContent.Trim());

        // Close button aria-label
        var closeBtn = cut.Find("button.agent-detail-close");
        Assert.Equal("Close drawer", closeBtn.GetAttribute("aria-label"));

        // Label filter: bug, feature, Clear
        var labelFilter = cut.Find(".drawer-label-filter");
        Assert.NotNull(labelFilter);
        var labelBtns = labelFilter.QuerySelectorAll("button");
        Assert.Contains(labelBtns, b => b.TextContent.Trim() == "bug");
        Assert.Contains(labelBtns, b => b.TextContent.Trim() == "feature");
        Assert.Contains(labelBtns, b => b.TextContent.Trim() == "Clear");

        // Two enabled btn-page buttons and Page 2 text
        var pageBtns = cut.FindAll("button.btn-page");
        Assert.Equal(2, pageBtns.Count);
        Assert.All(pageBtns, b => Assert.Null(b.GetAttribute("disabled")));
        Assert.Contains("Page 2", cut.Markup);

        // Row data-testid values
        cut.Find("[data-testid='issue-row-1']");
        cut.Find("[data-testid='issue-row-2']");

        // Dispatch button (requires selecting a row first)
        cut.Find("[data-testid='issue-row-1']").Click();
        cut.Find("[data-testid='dispatch-issue-btn']");
    }

    [Fact]
    public void PrDispatchDrawer_RendersFrameLabelFilterAndPager()
    {
        PullRequestSummary Pr(string id) => new()
        {
            Number = int.Parse(id), Identifier = id, Title = $"PR {id}", Description = "",
            Labels = [], BranchName = $"feat/{id}", TargetBranch = "main",
            Url = $"https://example.test/pull/{id}", IsDraft = false
        };
        var cut = Render<PrDispatchDrawer>(p => p
            .Add(c => c.IsOpen, true)
            .Add(c => c.Template, Template)
            .Add(c => c.PullRequests, new[] { Pr("1"), Pr("2") })
            .Add(c => c.IsLoading, false)
            .Add(c => c.IsDispatching, false)
            .Add(c => c.HasMore, true)
            .Add(c => c.Page, 2)
            .Add(c => c.AvailableLabels, new[] { "bug", "feature" })
            .Add(c => c.SelectedLabels, new[] { "bug" })
            .Add(c => c.GetProcessingStatus, _ => null));

        // Open aside without inert
        var aside = cut.Find("aside.dispatch-drawer.open");
        Assert.Null(aside.GetAttribute("inert"));

        // Open overlay
        cut.Find(".dispatch-drawer-overlay.open");

        // Title in h3
        Assert.Equal("Browse Pull Requests", cut.Find("h3").TextContent.Trim());

        // Close button aria-label
        var closeBtn = cut.Find("button.agent-detail-close");
        Assert.Equal("Close drawer", closeBtn.GetAttribute("aria-label"));

        // Label filter: bug, feature, Clear
        var labelFilter = cut.Find(".drawer-label-filter");
        Assert.NotNull(labelFilter);
        var labelBtns = labelFilter.QuerySelectorAll("button");
        Assert.Contains(labelBtns, b => b.TextContent.Trim() == "bug");
        Assert.Contains(labelBtns, b => b.TextContent.Trim() == "feature");
        Assert.Contains(labelBtns, b => b.TextContent.Trim() == "Clear");

        // Two enabled btn-page buttons and Page 2 text
        var pageBtns = cut.FindAll("button.btn-page");
        Assert.Equal(2, pageBtns.Count);
        Assert.All(pageBtns, b => Assert.Null(b.GetAttribute("disabled")));
        Assert.Contains("Page 2", cut.Markup);

        // Row data-testid values
        cut.Find("[data-testid='pr-row-1']");
        cut.Find("[data-testid='pr-row-2']");

        // Dispatch button (requires selecting a row first)
        cut.Find("[data-testid='pr-row-1']").Click();
        cut.Find("[data-testid='dispatch-pr-btn']");
    }
}
