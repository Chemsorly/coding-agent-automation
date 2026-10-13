using AwesomeAssertions;
using CodingAgent.Api.Browse;
using CodingAgent.Pipeline;
using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;
using Moq;
using Xunit;

namespace CodingAgent.Api.IntegrationTests.Browse;

/// <summary>
/// Spec 051 2.3b: the drawers' browsing moved from the web host's drawer services into the API. These
/// cover what the web's IssueDrawerServiceTests, PrReviewDrawerServiceTests and EpicDrawerServiceTests
/// covered at the provider level: paging and labels, a missing provider, the epic merge and tracker,
/// and the dependency readiness. The active-issue badges need the database; UiBrowseEndpointTests cover them.
/// </summary>
public class TemplateBrowseServiceTests
{
    private static readonly PipelineJobTemplate Template = new() { Id = "t-1", Name = "T", IssueProviderId = "ip-1", RepoProviderId = "rp-1" };
    private static readonly ProviderConfig IssueTracker = new() { Id = "ip-1", Kind = ProviderKind.Issue, ProviderType = "GitHub", DisplayName = "Issues" };
    private static readonly ProviderConfig EpicTracker = new() { Id = "epic-ip", Kind = ProviderKind.Issue, ProviderType = "GitHub", DisplayName = "Epics" };
    private static readonly ProviderConfig Repository = new() { Id = "rp-1", Kind = ProviderKind.Repository, ProviderType = "GitHub", DisplayName = "Repo" };
    private static readonly string[] BugLabel = ["bug"];

    private readonly Mock<IProjectStore> _projects = new();
    private readonly Mock<IProviderConfigStore> _configs = new();
    private readonly Mock<IProviderFactory> _factory = new();
    private readonly Mock<IDependencyChecker> _dependencies = new();
    private readonly Mock<IIssueProvider> _issues = new();

    public TemplateBrowseServiceTests()
    {
        _configs.Setup(c => c.GetProviderConfigByIdAsync("ip-1", ProviderKind.Issue, It.IsAny<CancellationToken>())).ReturnsAsync(IssueTracker);
        _configs.Setup(c => c.GetProviderConfigByIdAsync("epic-ip", ProviderKind.Issue, It.IsAny<CancellationToken>())).ReturnsAsync(EpicTracker);
        _configs.Setup(c => c.GetProviderConfigByIdAsync("rp-1", ProviderKind.Repository, It.IsAny<CancellationToken>())).ReturnsAsync(Repository);
        _factory.Setup(f => f.CreateIssueProvider(It.IsAny<ProviderConfig>())).Returns(_issues.Object);
    }

    private TemplateBrowseService Service() => new(_projects.Object, _configs.Object, _factory.Object, _dependencies.Object, workItems: null!);

    private static BrowseTemplate Browse(PipelineProject? project = null) => new(Template, project);

    private static PagedResult<IssueSummary> Page(params IssueSummary[] items) =>
        new() { Items = items, Page = 1, PageSize = 15, HasMore = true };

    private static IssueSummary Issue(string id, string? description = null, params string[] labels) =>
        new() { Identifier = id, Title = $"Issue {id}", Labels = labels, Description = description };

    [Fact]
    public async Task FindAsync_ReturnsTheTemplate_AndTheProjectThatOwnsIt()
    {
        var owner = new PipelineProject { Id = "p-1", Name = "P", TemplateIds = ["t-1"] };
        _projects.Setup(p => p.LoadAllTemplatesAsync(It.IsAny<CancellationToken>())).ReturnsAsync([Template]);
        _projects.Setup(p => p.LoadProjectsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([new PipelineProject { Id = "p-0", Name = "Other", TemplateIds = ["t-9"] }, owner]);

        var found = await Service().FindAsync("t-1", CancellationToken.None);
        var missing = await Service().FindAsync("t-404", CancellationToken.None);

        found.Should().Be(new BrowseTemplate(Template, owner));
        missing.Should().BeNull();
    }

    [Fact]
    public async Task ListIssuesAsync_PagesTheTrackersOpenIssues_WithTheSelectedLabels()
    {
        _issues.Setup(p => p.ListOpenIssuesAsync(2, TemplateBrowseService.PageSize, It.Is<IReadOnlyList<string>?>(l => l != null && l.SequenceEqual(BugLabel)), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Page(Issue("7")));

        var page = await Service().ListIssuesAsync(Browse(), 2, BugLabel, CancellationToken.None);

        page.Items.Select(i => i.Identifier).Should().Equal("7");
        page.HasMore.Should().BeTrue();
    }

    [Fact]
    public async Task ListIssuesAsync_WithoutLabels_PassesNone()
    {
        _issues.Setup(p => p.ListOpenIssuesAsync(1, TemplateBrowseService.PageSize, null, It.IsAny<CancellationToken>())).ReturnsAsync(Page());

        await Service().ListIssuesAsync(Browse(), 1, [], CancellationToken.None);

        _issues.Verify(p => p.ListOpenIssuesAsync(1, TemplateBrowseService.PageSize, null, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ListIssuesAsync_WhenTheProviderIsGone_SaysSo()
    {
        _configs.Setup(c => c.GetProviderConfigByIdAsync("ip-1", ProviderKind.Issue, It.IsAny<CancellationToken>())).ReturnsAsync((ProviderConfig?)null);

        var list = () => Service().ListIssuesAsync(Browse(), 1, null, CancellationToken.None);

        (await list.Should().ThrowAsync<TemplateBrowseException>()).Which.Message.Should().Be("Issue provider not found for this template.");
    }

    [Fact]
    public async Task ListIssuesAsync_WhenTheProviderFails_NamesWhatFailed()
    {
        _issues.Setup(p => p.ListOpenIssuesAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<IReadOnlyList<string>?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("rate limited"));

        var list = () => Service().ListIssuesAsync(Browse(), 1, null, CancellationToken.None);

        (await list.Should().ThrowAsync<TemplateBrowseException>()).Which.Message.Should().Be("Failed to load issues: rate limited");
    }

    [Fact]
    public async Task CheckReadinessAsync_ChecksEachIssueOfThePage_SharingTheStateCache()
    {
        _issues.Setup(p => p.ListOpenIssuesAsync(1, TemplateBrowseService.PageSize, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Page(Issue("10", "Blocked by #5"), Issue("11", "No deps")));
        var blocked = new DependencyCheckResult { IsReady = false, BlockedBy = [5], TotalDependencies = 1 };
        var caches = new List<Dictionary<int, bool>>();
        _dependencies.Setup(d => d.CheckAsync("10", "Blocked by #5", _issues.Object, It.IsAny<Dictionary<int, bool>>(), It.IsAny<CancellationToken>()))
            .Callback<IssueIdentifier, string?, IIssueProvider, Dictionary<int, bool>, CancellationToken>((_, _, _, cache, _) => caches.Add(cache))
            .ReturnsAsync(blocked);
        _dependencies.Setup(d => d.CheckAsync("11", "No deps", _issues.Object, It.IsAny<Dictionary<int, bool>>(), It.IsAny<CancellationToken>()))
            .Callback<IssueIdentifier, string?, IIssueProvider, Dictionary<int, bool>, CancellationToken>((_, _, _, cache, _) => caches.Add(cache))
            .ReturnsAsync(DependencyCheckResult.NoDependencies);

        var readiness = await Service().CheckReadinessAsync(Browse(), 1, null, CancellationToken.None);

        readiness["10"].Should().BeSameAs(blocked);
        readiness["11"].IsReady.Should().BeTrue();
        caches.Should().HaveCount(2).And.OnlyContain(c => ReferenceEquals(c, caches[0]));
    }

    [Fact]
    public async Task CheckReadinessAsync_WhenACheckFails_KeepsTheIssuesCheckedSoFar()
    {
        _issues.Setup(p => p.ListOpenIssuesAsync(1, TemplateBrowseService.PageSize, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Page(Issue("10"), Issue("11")));
        _dependencies.Setup(d => d.CheckAsync("10", It.IsAny<string?>(), It.IsAny<IIssueProvider>(), It.IsAny<Dictionary<int, bool>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(DependencyCheckResult.NoDependencies);
        _dependencies.Setup(d => d.CheckAsync("11", It.IsAny<string?>(), It.IsAny<IIssueProvider>(), It.IsAny<Dictionary<int, bool>>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("rate limited"));

        var readiness = await Service().CheckReadinessAsync(Browse(), 1, null, CancellationToken.None);

        readiness.Keys.Should().Equal("10");
    }

    [Fact]
    public async Task ListLabelsAsync_ReturnsTheTrackersLabels()
    {
        _issues.Setup(p => p.ListRepositoryLabelsAsync(It.IsAny<CancellationToken>())).ReturnsAsync(["bug", "agent:epic"]);

        var labels = await Service().ListLabelsAsync(Browse(), CancellationToken.None);

        labels.Should().Equal("bug", "agent:epic");
    }

    [Fact]
    public async Task ListPullRequestsAsync_PagesTheRepositorysOpenPullRequests()
    {
        var repository = new Mock<IRepositoryProvider>();
        repository.Setup(r => r.ListOpenPullRequestsAsync(1, TemplateBrowseService.PageSize, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PagedResult<PullRequestSummary>
            {
                Items = [new PullRequestSummary { Identifier = "5", Number = 5, Title = "PR 5", BranchName = "feat/x", TargetBranch = "main", Url = "http://x", Description = "", Labels = [], IsDraft = false }],
                Page = 1, PageSize = 15, HasMore = false,
            });
        _factory.Setup(f => f.CreateRepositoryProvider(Repository)).Returns(repository.Object);

        var page = await Service().ListPullRequestsAsync(Browse(), 1, null, CancellationToken.None);

        page.Items.Select(p => p.Number).Should().Equal(5);
    }

    [Fact]
    public async Task ListPullRequestsAsync_WhenTheRepositoryIsGone_SaysSo()
    {
        _configs.Setup(c => c.GetProviderConfigByIdAsync("rp-1", ProviderKind.Repository, It.IsAny<CancellationToken>())).ReturnsAsync((ProviderConfig?)null);

        var list = () => Service().ListPullRequestsAsync(Browse(), 1, null, CancellationToken.None);

        (await list.Should().ThrowAsync<TemplateBrowseException>()).Which.Message.Should().Be("Repository provider not found for this template.");
    }

    [Fact]
    public async Task ListEpicsAsync_MergesTheNewAndTheApprovedEpics_ByIdentifier()
    {
        // An epic with both labels comes back from both queries; it is listed once.
        var both = Issue("100", null, AgentLabels.Epic, AgentLabels.EpicApproved);
        _issues.Setup(p => p.ListOpenIssuesAsync(1, TemplateBrowseService.EpicPageSize,
                It.Is<IReadOnlyList<string>?>(l => l != null && l.Contains(AgentLabels.Epic) && !l.Contains(AgentLabels.EpicApproved)), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PagedResult<IssueSummary> { Items = [both, Issue("101", null, AgentLabels.Epic)], Page = 1, PageSize = 8, HasMore = false });
        _issues.Setup(p => p.ListOpenIssuesAsync(1, TemplateBrowseService.EpicPageSize,
                It.Is<IReadOnlyList<string>?>(l => l != null && l.Contains(AgentLabels.EpicApproved)), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PagedResult<IssueSummary> { Items = [both, Issue("102", null, AgentLabels.EpicApproved)], Page = 1, PageSize = 8, HasMore = true });

        var page = await Service().ListEpicsAsync(Browse(), 1, null, CancellationToken.None);

        page.Items.Select(i => i.Identifier).Should().Equal("100", "101", "102");
        page.HasMore.Should().BeTrue();
    }

    [Fact]
    public async Task ListEpicsAsync_AddsTheSelectedLabelsToBothQueries()
    {
        var queried = new List<IReadOnlyList<string>?>();
        _issues.Setup(p => p.ListOpenIssuesAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<IReadOnlyList<string>?>(), It.IsAny<CancellationToken>()))
            .Callback<int, int, IReadOnlyList<string>?, CancellationToken>((_, _, labels, _) => queried.Add(labels))
            .ReturnsAsync(new PagedResult<IssueSummary> { Items = [], Page = 1, PageSize = 8, HasMore = false });

        await Service().ListEpicsAsync(Browse(), 1, BugLabel, CancellationToken.None);

        queried.Should().BeEquivalentTo(new[] { new[] { AgentLabels.Epic, "bug" }, new[] { AgentLabels.EpicApproved, "bug" } });
    }

    [Fact]
    public async Task Epics_ComeFromTheProjectsEpicTracker_WhenItHasOne()
    {
        var epics = new Mock<IIssueProvider>();
        epics.Setup(p => p.ListOpenIssuesAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<IReadOnlyList<string>?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PagedResult<IssueSummary> { Items = [Issue("300")], Page = 1, PageSize = 8, HasMore = false });
        epics.Setup(p => p.ListRepositoryLabelsAsync(It.IsAny<CancellationToken>())).ReturnsAsync(["backend", AgentLabels.Epic, AgentLabels.EpicApproved]);
        _factory.Setup(f => f.CreateIssueProvider(EpicTracker)).Returns(epics.Object);
        var project = new PipelineProject { Id = "p-1", Name = "P", TemplateIds = ["t-1"], EpicIssueProviderId = "epic-ip" };

        var page = await Service().ListEpicsAsync(Browse(project), 1, null, CancellationToken.None);
        var labels = await Service().ListEpicLabelsAsync(Browse(project), CancellationToken.None);

        page.Items.Select(i => i.Identifier).Should().Equal("300");
        labels.Should().Equal("backend");
        TemplateBrowseService.EpicTrackerOf(Browse(project)).Should().Be("epic-ip");
        TemplateBrowseService.EpicTrackerOf(Browse(new PipelineProject { Id = "p-2", Name = "Q" })).Should().Be("ip-1");
    }

    [Fact]
    public async Task ListEpicsAsync_WhenTheEpicTrackerIsGone_SaysSo()
    {
        var project = new PipelineProject { Id = "p-1", Name = "P", EpicIssueProviderId = "gone" };

        var list = () => Service().ListEpicsAsync(Browse(project), 1, null, CancellationToken.None);

        (await list.Should().ThrowAsync<TemplateBrowseException>()).Which.Message.Should().Be("Epic issue provider not found.");
    }
}
