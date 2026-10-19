using AwesomeAssertions;
using Moq;
using CodingAgent.Pipeline.Models;
using CodingAgent.Web.Services;

namespace CodingAgent.Web.UnitTests.Services;

/// <summary>
/// Unit tests for <see cref="DrawerStatePresenter"/>.
/// Verifies that every accessor property correctly delegates to the underlying
/// drawer services provided via <see cref="DispatchDrawers"/>.
/// </summary>
public class DrawerStatePresenterTests
{
    private readonly Mock<IIssueDrawerService> _mockIssueDrawerService = new();
    private readonly Mock<IPrReviewDrawerService> _mockPrReviewDrawerService = new();
    private readonly Mock<IEpicDrawerService> _mockEpicDrawerService = new();

    private readonly DrawerStateService<IssueSummary> _issueDrawerState;
    private readonly DrawerStateService<PullRequestSummary> _prDrawerState;
    private readonly DrawerStateService<IssueSummary> _epicDrawerState;

    private readonly DrawerStatePresenter _presenter;

    public DrawerStatePresenterTests()
    {
        _issueDrawerState = new DrawerStateService<IssueSummary>(
            _ => Task.FromResult<string?>(null),
            _ => Task.FromResult<string?>(null),
            (_, _) => Task.FromResult<(bool, string?, string?)>((true, null, null)));

        _prDrawerState = new DrawerStateService<PullRequestSummary>(
            _ => Task.FromResult<string?>(null),
            _ => Task.FromResult<string?>(null),
            (_, _) => Task.FromResult<(bool, string?, string?)>((true, null, null)));

        _epicDrawerState = new DrawerStateService<IssueSummary>(
            _ => Task.FromResult<string?>(null),
            _ => Task.FromResult<string?>(null),
            (_, _) => Task.FromResult<(bool, string?, string?)>((true, null, null)));

        _mockIssueDrawerService.Setup(s => s.DrawerState).Returns(_issueDrawerState);
        _mockIssueDrawerService.Setup(s => s.DrawerReadiness).Returns(new Dictionary<string, DependencyCheckResult>());
        _mockPrReviewDrawerService.Setup(s => s.DrawerState).Returns(_prDrawerState);
        _mockEpicDrawerService.Setup(s => s.DrawerState).Returns(_epicDrawerState);

        _presenter = new DrawerStatePresenter(
            new DispatchDrawers(
                _mockIssueDrawerService.Object,
                _mockPrReviewDrawerService.Object,
                _mockEpicDrawerService.Object));
    }

    // ── IsOpen ──

    [Fact]
    public void IsIssueDrawerOpen_ReturnsTrue_WhenIssueDrawerIsOpen()
    {
        _issueDrawerState.IsOpen = true;
        _presenter.IsIssueDrawerOpen.Should().BeTrue();
    }

    [Fact]
    public void IsIssueDrawerOpen_ReturnsFalse_WhenIssueDrawerIsClosed()
    {
        _issueDrawerState.IsOpen = false;
        _presenter.IsIssueDrawerOpen.Should().BeFalse();
    }

    [Fact]
    public void IsPrDrawerOpen_ReturnsTrue_WhenPrDrawerIsOpen()
    {
        _prDrawerState.IsOpen = true;
        _presenter.IsPrDrawerOpen.Should().BeTrue();
    }

    [Fact]
    public void IsPrDrawerOpen_ReturnsFalse_WhenPrDrawerIsClosed()
    {
        _prDrawerState.IsOpen = false;
        _presenter.IsPrDrawerOpen.Should().BeFalse();
    }

    [Fact]
    public void IsEpicDrawerOpen_ReturnsTrue_WhenEpicDrawerIsOpen()
    {
        _epicDrawerState.IsOpen = true;
        _presenter.IsEpicDrawerOpen.Should().BeTrue();
    }

    [Fact]
    public void IsEpicDrawerOpen_ReturnsFalse_WhenEpicDrawerIsClosed()
    {
        _epicDrawerState.IsOpen = false;
        _presenter.IsEpicDrawerOpen.Should().BeFalse();
    }

    // ── Templates ──

    [Fact]
    public void IssueDrawerTemplate_ReturnsTemplateFromIssueDrawerState()
    {
        var template = new PipelineJobTemplate { Id = "t-1", Name = "Issue Template", IssueProviderId = "ip-1", RepoProviderId = "rp-1" };
        _issueDrawerState.Template = template;
        _presenter.IssueDrawerTemplate.Should().BeSameAs(template);
    }

    [Fact]
    public void PrDrawerTemplate_ReturnsTemplateFromPrDrawerState()
    {
        var template = new PipelineJobTemplate { Id = "t-2", Name = "PR Template", IssueProviderId = "ip-1", RepoProviderId = "rp-1" };
        _prDrawerState.Template = template;
        _presenter.PrDrawerTemplate.Should().BeSameAs(template);
    }

    [Fact]
    public void EpicDrawerTemplate_ReturnsTemplateFromEpicDrawerState()
    {
        var template = new PipelineJobTemplate { Id = "t-3", Name = "Epic Template", IssueProviderId = "ip-1", RepoProviderId = "rp-1" };
        _epicDrawerState.Template = template;
        _presenter.EpicDrawerTemplate.Should().BeSameAs(template);
    }

    // ── IsDispatching ──

    [Fact]
    public void IssueDrawerDispatching_Getter_ReadsFromIssueDrawerState()
    {
        _issueDrawerState.IsDispatching = true;
        _presenter.IssueDrawerDispatching.Should().BeTrue();
    }

    [Fact]
    public void IssueDrawerDispatching_Setter_WritesToIssueDrawerState()
    {
        _presenter.IssueDrawerDispatching = true;
        _issueDrawerState.IsDispatching.Should().BeTrue();
    }

    [Fact]
    public void PrDrawerDispatching_Getter_ReadsFromPrDrawerState()
    {
        _prDrawerState.IsDispatching = true;
        _presenter.PrDrawerDispatching.Should().BeTrue();
    }

    [Fact]
    public void PrDrawerDispatching_Setter_WritesToPrDrawerState()
    {
        _presenter.PrDrawerDispatching = true;
        _prDrawerState.IsDispatching.Should().BeTrue();
    }

    [Fact]
    public void EpicDrawerDispatching_Getter_ReadsFromEpicDrawerState()
    {
        _epicDrawerState.IsDispatching = true;
        _presenter.EpicDrawerDispatching.Should().BeTrue();
    }

    [Fact]
    public void EpicDrawerDispatching_Setter_WritesToEpicDrawerState()
    {
        _presenter.EpicDrawerDispatching = true;
        _epicDrawerState.IsDispatching.Should().BeTrue();
    }

    // ── Issue drawer items ──

    [Fact]
    public void DrawerIssues_ReturnsSameListAsIssueDrawerState()
    {
        _issueDrawerState.Items.Add(new IssueSummary { Identifier = "1", Title = "T", Labels = [] });
        _presenter.DrawerIssues.Should().BeSameAs(_issueDrawerState.Items);
    }

    [Fact]
    public void DrawerPage_ReturnsPageFromIssueDrawerState()
    {
        _issueDrawerState.Page = 3;
        _presenter.DrawerPage.Should().Be(3);
    }

    [Fact]
    public void DrawerHasMore_ReturnsHasMoreFromIssueDrawerState()
    {
        _issueDrawerState.HasMore = true;
        _presenter.DrawerHasMore.Should().BeTrue();
    }

    [Fact]
    public void DrawerLoading_ReturnsLoadingFromIssueDrawerState()
    {
        _issueDrawerState.Loading = true;
        _presenter.DrawerLoading.Should().BeTrue();
    }

    [Fact]
    public void DrawerLabels_ReturnsSameListAsIssueDrawerState()
    {
        _issueDrawerState.Labels.Add("bug");
        _presenter.DrawerLabels.Should().BeSameAs(_issueDrawerState.Labels);
    }

    [Fact]
    public void DrawerSelectedLabels_ReturnsSameListAsIssueDrawerState()
    {
        _issueDrawerState.ToggleLabel("bug");
        _presenter.DrawerSelectedLabels.Should().BeSameAs(_issueDrawerState.SelectedLabels);
    }

    [Fact]
    public void DrawerReadiness_ReturnsDictionaryFromIssueDrawerService()
    {
        var readiness = new Dictionary<string, DependencyCheckResult>
        {
            ["issue-1"] = new DependencyCheckResult { IsReady = true, BlockedBy = [], TotalDependencies = 0 }
        };
        _mockIssueDrawerService.Setup(s => s.DrawerReadiness).Returns(readiness);

        _presenter.DrawerReadiness.Should().BeSameAs(readiness);
    }

    // ── PR drawer items ──

    [Fact]
    public void PrDrawerPrs_ReturnsSameListAsPrDrawerState()
    {
        _prDrawerState.Items.Add(new PullRequestSummary
        {
            Number = 1,
            Identifier = "pr-1",
            Title = "PR",
            Description = "",
            Labels = [],
            BranchName = "feature/test",
            TargetBranch = "main",
            Url = "https://github.com/test/pr/1",
            IsDraft = false
        });
        _presenter.PrDrawerPrs.Should().BeSameAs(_prDrawerState.Items);
    }

    [Fact]
    public void PrDrawerPage_ReturnsPageFromPrDrawerState()
    {
        _prDrawerState.Page = 2;
        _presenter.PrDrawerPage.Should().Be(2);
    }

    [Fact]
    public void PrDrawerHasMore_ReturnsHasMoreFromPrDrawerState()
    {
        _prDrawerState.HasMore = true;
        _presenter.PrDrawerHasMore.Should().BeTrue();
    }

    [Fact]
    public void PrDrawerLoading_ReturnsLoadingFromPrDrawerState()
    {
        _prDrawerState.Loading = true;
        _presenter.PrDrawerLoading.Should().BeTrue();
    }

    [Fact]
    public void PrDrawerLabels_ReturnsSameListAsPrDrawerState()
    {
        _prDrawerState.Labels.Add("urgent");
        _presenter.PrDrawerLabels.Should().BeSameAs(_prDrawerState.Labels);
    }

    [Fact]
    public void PrDrawerSelectedLabels_ReturnsSameListAsPrDrawerState()
    {
        _prDrawerState.ToggleLabel("urgent");
        _presenter.PrDrawerSelectedLabels.Should().BeSameAs(_prDrawerState.SelectedLabels);
    }

    // ── Epic drawer items ──

    [Fact]
    public void EpicDrawerIssues_ReturnsSameListAsEpicDrawerState()
    {
        _epicDrawerState.Items.Add(new IssueSummary { Identifier = "epic-1", Title = "E", Labels = [] });
        _presenter.EpicDrawerIssues.Should().BeSameAs(_epicDrawerState.Items);
    }

    [Fact]
    public void EpicDrawerPage_ReturnsPageFromEpicDrawerState()
    {
        _epicDrawerState.Page = 4;
        _presenter.EpicDrawerPage.Should().Be(4);
    }

    [Fact]
    public void EpicDrawerHasMore_ReturnsHasMoreFromEpicDrawerState()
    {
        _epicDrawerState.HasMore = true;
        _presenter.EpicDrawerHasMore.Should().BeTrue();
    }

    [Fact]
    public void EpicDrawerLoading_ReturnsLoadingFromEpicDrawerState()
    {
        _epicDrawerState.Loading = true;
        _presenter.EpicDrawerLoading.Should().BeTrue();
    }

    [Fact]
    public void EpicDrawerLabels_ReturnsSameListAsEpicDrawerState()
    {
        _epicDrawerState.Labels.Add("epic");
        _presenter.EpicDrawerLabels.Should().BeSameAs(_epicDrawerState.Labels);
    }

    [Fact]
    public void EpicDrawerSelectedLabels_ReturnsSameListAsEpicDrawerState()
    {
        _epicDrawerState.ToggleLabel("epic");
        _presenter.EpicDrawerSelectedLabels.Should().BeSameAs(_epicDrawerState.SelectedLabels);
    }
}
