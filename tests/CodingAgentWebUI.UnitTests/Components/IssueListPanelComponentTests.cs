using Bunit;
using Moq;
using Microsoft.AspNetCore.Components;
using CodingAgentWebUI.Components.Pages;
using CodingAgentWebUI.Pipeline;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;

namespace CodingAgentWebUI.UnitTests.Components;

/// <summary>
/// bUnit component tests for the IssueListPanel component.
/// Covers provider dropdown, issue loading, label filtering, pagination, and issue selection.
/// </summary>
public class IssueListPanelComponentTests : BunitContext
{
    private readonly Mock<IProviderFactory> _mockProviderFactory;
    private readonly Mock<IIssueProvider> _mockIssueProvider;
    private readonly List<ProviderConfig> _issueProviders;

    public IssueListPanelComponentTests()
    {
        _mockProviderFactory = new Mock<IProviderFactory>();
        _mockIssueProvider = new Mock<IIssueProvider>() { CallBase = true };

        _issueProviders = new List<ProviderConfig>
        {
            new()
            {
                Id = "provider-1",
                Kind = ProviderKind.Issue,
                ProviderType = "GitHub",
                DisplayName = "Test Issues",
                Settings = new Dictionary<string, string>
                {
                    [ProviderSettingKeys.Owner] = "testorg",
                    [ProviderSettingKeys.Repo] = "testrepo"
                }
            }
        };

        _mockProviderFactory
            .Setup(f => f.CreateIssueProvider(It.IsAny<ProviderConfig>()))
            .Returns(_mockIssueProvider.Object);

        _mockIssueProvider
            .Setup(p => p.DisposeAsync())
            .Returns(ValueTask.CompletedTask);
    }

    private void SetupIssueList(List<IssueSummary>? issues = null, bool hasMore = false)
    {
        var items = issues ?? new List<IssueSummary>
        {
            new() { Identifier = "1", Title = "First Issue", Labels = new List<string> { "bug" } },
            new() { Identifier = "2", Title = "Second Issue", Labels = new List<string> { "feature", "agent:next" } },
            new() { Identifier = "3", Title = "Third Issue", Labels = new List<string>() }
        };

        _mockIssueProvider
            .Setup(p => p.ListOpenIssuesAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<IReadOnlyList<string>?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PagedResult<IssueSummary>
            {
                Items = items,
                Page = 1,
                PageSize = 25,
                HasMore = hasMore
            });
    }

    /// <summary>Helper to render and select a provider via the dropdown, triggering async load.</summary>
    private async Task<IRenderedComponent<IssueListPanel>> RenderAndSelectProvider(
        List<IssueSummary>? issues = null, bool hasMore = false,
        Action<ComponentParameterCollectionBuilder<IssueListPanel>>? additionalParams = null)
    {
        SetupIssueList(issues, hasMore);

        var cut = Render<IssueListPanel>(p =>
        {
            p.Add(s => s.IssueProviders, _issueProviders);
            p.Add(s => s.ProviderFactory, _mockProviderFactory.Object);
            additionalParams?.Invoke(p);
        });

        // Trigger provider selection via the dropdown
        var select = cut.Find("select");
        await cut.InvokeAsync(() => select.Change("provider-1"));

        return cut;
    }

    // ═══ Provider Dropdown ═══

    [Fact]
    public void RendersProviderDropdown_WithIssueProviders()
    {
        var cut = Render<IssueListPanel>(p => p
            .Add(s => s.IssueProviders, _issueProviders)
            .Add(s => s.ProviderFactory, _mockProviderFactory.Object));

        var select = cut.Find("select");
        Assert.NotNull(select);
        Assert.Contains("Test Issues", cut.Markup);
        Assert.Contains("testorg/testrepo", cut.Markup);
    }

    [Fact]
    public void RendersDefaultOption_SelectProvider()
    {
        var cut = Render<IssueListPanel>(p => p
            .Add(s => s.IssueProviders, _issueProviders)
            .Add(s => s.ProviderFactory, _mockProviderFactory.Object));

        Assert.Contains("-- Select Provider --", cut.Markup);
    }

    [Fact]
    public void RendersMultipleProviders_InDropdown()
    {
        var providers = new List<ProviderConfig>
        {
            new() { Id = "p1", Kind = ProviderKind.Issue, ProviderType = "GitHub", DisplayName = "Provider A",
                Settings = new() { [ProviderSettingKeys.Owner] = "org1", [ProviderSettingKeys.Repo] = "repo1" } },
            new() { Id = "p2", Kind = ProviderKind.Issue, ProviderType = "GitHub", DisplayName = "Provider B",
                Settings = new() { [ProviderSettingKeys.Owner] = "org2", [ProviderSettingKeys.Repo] = "repo2" } }
        };

        var cut = Render<IssueListPanel>(p => p
            .Add(s => s.IssueProviders, providers)
            .Add(s => s.ProviderFactory, _mockProviderFactory.Object));

        Assert.Contains("Provider A", cut.Markup);
        Assert.Contains("Provider B", cut.Markup);
    }

    // ═══ Issue List Rendering ═══

    [Fact]
    public async Task ShowsIssueList_AfterProviderSelected()
    {
        var cut = await RenderAndSelectProvider();

        // agent:next auto-selects as filter, so only issue #2 with that label is visible
        Assert.Contains("Second Issue", cut.Markup);
        // The other issues are filtered out but exist in the data
        Assert.Contains("Showing", cut.Markup);
    }

    [Fact]
    public async Task ShowsIssueIdentifiers_InList()
    {
        // Use issues without agent:next to avoid auto-filtering
        var cut = await RenderAndSelectProvider(issues: new List<IssueSummary>
        {
            new() { Identifier = "1", Title = "First Issue", Labels = new List<string> { "bug" } },
            new() { Identifier = "2", Title = "Second Issue", Labels = new List<string> { "feature" } },
            new() { Identifier = "3", Title = "Third Issue", Labels = new List<string>() }
        });

        Assert.Contains("#1", cut.Markup);
        Assert.Contains("#2", cut.Markup);
        Assert.Contains("#3", cut.Markup);
    }

    [Fact]
    public async Task ShowsIssueLabels_InList()
    {
        var cut = await RenderAndSelectProvider();

        Assert.Contains("bug", cut.Markup);
        Assert.Contains("feature", cut.Markup);
    }

    // ═══ Label Filter Chips ═══

    [Fact]
    public async Task ShowsLabelFilterChips_WhenIssuesHaveLabels()
    {
        var cut = await RenderAndSelectProvider();

        Assert.Contains("Filter by labels:", cut.Markup);
        // Agent labels are always shown
        Assert.Contains("agent:next", cut.Markup);
    }

    [Fact]
    public async Task ShowsFilterCount_WhenLabelsSelected()
    {
        var cut = await RenderAndSelectProvider();

        // agent:next is auto-selected when issues have that label
        Assert.Contains("Showing", cut.Markup);
        Assert.Contains("of", cut.Markup);
    }

    // ═══ Pagination ═══

    [Fact]
    public async Task ShowsPaginationButtons()
    {
        var cut = await RenderAndSelectProvider(hasMore: true);

        Assert.Contains("Previous", cut.Markup);
        Assert.Contains("Next", cut.Markup);
        Assert.Contains("Page 1", cut.Markup);
    }

    [Fact]
    public async Task PreviousButton_DisabledOnFirstPage()
    {
        var cut = await RenderAndSelectProvider(hasMore: true);

        var prevButton = cut.FindAll("button.btn-page").First(b => b.TextContent.Contains("Previous"));
        Assert.True(prevButton.HasAttribute("disabled"));
    }

    [Fact]
    public async Task NextButton_EnabledWhenHasMore()
    {
        var cut = await RenderAndSelectProvider(hasMore: true);

        var nextButton = cut.FindAll("button.btn-page").First(b => b.TextContent.Contains("Next"));
        Assert.False(nextButton.HasAttribute("disabled"));
    }

    [Fact]
    public async Task NextButton_DisabledWhenNoMore()
    {
        var cut = await RenderAndSelectProvider(hasMore: false);

        var nextButton = cut.FindAll("button.btn-page").First(b => b.TextContent.Contains("Next"));
        Assert.True(nextButton.HasAttribute("disabled"));
    }

    // ═══ Issue Selection ═══

    [Fact]
    public async Task ClickingIssue_TriggersOnIssueSelected()
    {
        _mockIssueProvider
            .Setup(p => p.GetIssueAsync("1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new IssueDetail
            {
                Identifier = "1",
                Title = "First Issue",
                Description = "Issue description",
                Labels = new List<string> { "bug" }
            });

        (string IssueId, IssueDetail? Detail) selectedIssue = default;
        var cut = await RenderAndSelectProvider(additionalParams: p =>
            p.Add(s => s.OnIssueSelected, EventCallback.Factory.Create<(string, IssueDetail?)>(this, v => selectedIssue = v)));

        // Clear the label filter to show all issues (agent:next auto-selects)
        var clearBtn = cut.FindAll("button").FirstOrDefault(b => b.TextContent.Contains("Clear all"));
        if (clearBtn != null)
            await cut.InvokeAsync(() => clearBtn.Click());

        var issueCard = cut.FindAll(".issue-card").First();
        await cut.InvokeAsync(() => issueCard.Click());

        Assert.Equal("1", selectedIssue.IssueId);
        Assert.NotNull(selectedIssue.Detail);
        Assert.Equal("First Issue", selectedIssue.Detail!.Title);
    }

    [Fact]
    public async Task ProviderChange_TriggersOnIssueProviderIdChanged()
    {
        SetupIssueList();
        string changedProviderId = "";

        var cut = Render<IssueListPanel>(p => p
            .Add(s => s.IssueProviders, _issueProviders)
            .Add(s => s.ProviderFactory, _mockProviderFactory.Object)
            .Add(s => s.OnIssueProviderIdChanged, EventCallback.Factory.Create<string>(this, v => changedProviderId = v)));

        var select = cut.Find("select");
        await cut.InvokeAsync(() => select.Change("provider-1"));

        Assert.Equal("provider-1", changedProviderId);
    }

    // ═══ Error Handling ═══

    [Fact]
    public async Task FetchError_TriggersOnError()
    {
        _mockIssueProvider
            .Setup(p => p.ListOpenIssuesAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<IReadOnlyList<string>?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("API rate limit exceeded"));

        string? errorMessage = null;
        var cut = Render<IssueListPanel>(p => p
            .Add(s => s.IssueProviders, _issueProviders)
            .Add(s => s.ProviderFactory, _mockProviderFactory.Object)
            .Add(s => s.OnError, EventCallback.Factory.Create<string?>(this, v => errorMessage = v)));

        var select = cut.Find("select");
        await cut.InvokeAsync(() => select.Change("provider-1"));

        Assert.NotNull(errorMessage);
        Assert.Contains("API rate limit exceeded", errorMessage);
    }

    // ═══ Empty State ═══

    [Fact]
    public async Task EmptyIssueList_DoesNotShowIssueCards()
    {
        var cut = await RenderAndSelectProvider(issues: new List<IssueSummary>());

        var issueCards = cut.FindAll(".issue-card");
        Assert.Empty(issueCards);
    }

    [Fact]
    public void NoProviderSelected_DoesNotShowIssueList()
    {
        var cut = Render<IssueListPanel>(p => p
            .Add(s => s.IssueProviders, _issueProviders)
            .Add(s => s.ProviderFactory, _mockProviderFactory.Object));

        Assert.DoesNotContain("issue-card", cut.Markup);
        Assert.DoesNotContain("Loading issues...", cut.Markup);
    }

    // ═══ Processing/Queued Badge ═══

    [Fact]
    public async Task ShowsProcessingBadge_WhenIssueIsProcessing()
    {
        var cut = await RenderAndSelectProvider(
            issues: new List<IssueSummary>
            {
                new() { Identifier = "42", Title = "Processing Issue", Labels = new List<string>() }
            },
            additionalParams: p =>
                p.Add(s => s.IsIssueProcessingOrQueued, (Func<string, bool>)(id => id == "42")));

        Assert.Contains("issue-dispatched", cut.Markup);
    }

    // ═══ Selected Issue Highlight ═══

    [Fact]
    public async Task HighlightsSelectedIssue()
    {
        var cut = await RenderAndSelectProvider(
            issues: new List<IssueSummary>
            {
                new() { Identifier = "10", Title = "Selected One", Labels = new List<string>() }
            },
            additionalParams: p =>
                p.Add(s => s.SelectedIssueId, "10"));

        Assert.Contains("issue-selected", cut.Markup);
    }
}
