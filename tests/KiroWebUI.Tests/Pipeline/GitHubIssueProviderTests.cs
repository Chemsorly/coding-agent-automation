using AwesomeAssertions;
using Moq;
using Octokit;
using KiroWebUI.Pipeline.Providers;

namespace KiroWebUI.Tests.Pipeline;

public class GitHubIssueProviderTests
{
    private readonly Mock<IGitHubClient> _mockClient;
    private readonly Mock<IIssuesClient> _mockIssues;
    private readonly GitHubIssueProvider _provider;

    public GitHubIssueProviderTests()
    {
        _mockClient = new Mock<IGitHubClient>();
        _mockIssues = new Mock<IIssuesClient>();
        _mockClient.Setup(c => c.Issue).Returns(_mockIssues.Object);
        _provider = new GitHubIssueProvider(_mockClient.Object, "owner", "repo");
    }

    [Fact]
    public async Task GetIssueAsync_WithMissingBody_ReturnsEmptyDescription()
    {
        var issue = CreateOctokitIssue(42, "Test Issue", body: null, labels: []);
        _mockIssues.Setup(i => i.Get("owner", "repo", 42)).ReturnsAsync(issue);

        var result = await _provider.GetIssueAsync("42", CancellationToken.None);

        result.Description.Should().BeEmpty();
        result.Title.Should().Be("Test Issue");
        result.Identifier.Should().Be("42");
        result.AcceptanceCriteria.Should().BeEmpty();
    }

    [Fact]
    public async Task GetIssueAsync_MapsLabelsCorrectly()
    {
        var labels = new[] { "bug", "priority-high", "backend" };
        var issue = CreateOctokitIssue(10, "Bug Fix", body: "Fix the thing", labels: labels);
        _mockIssues.Setup(i => i.Get("owner", "repo", 10)).ReturnsAsync(issue);

        var result = await _provider.GetIssueAsync("10", CancellationToken.None);

        result.Labels.Should().BeEquivalentTo(labels);
    }

    [Fact]
    public async Task ListOpenIssuesAsync_MapsIssuesToSummaries()
    {
        var issues = new List<Issue>
        {
            CreateOctokitIssue(1, "First", body: "body1", labels: ["feat"]),
            CreateOctokitIssue(2, "Second", body: "body2", labels: [])
        };

        _mockIssues
            .Setup(i => i.GetAllForRepository("owner", "repo", It.IsAny<RepositoryIssueRequest>(), It.IsAny<ApiOptions>()))
            .ReturnsAsync(issues.AsReadOnly());

        var result = await _provider.ListOpenIssuesAsync(1, 25, CancellationToken.None);

        result.Items.Should().HaveCount(2);
        result.Items[0].Identifier.Should().Be("1");
        result.Items[0].Title.Should().Be("First");
        result.Items[0].Labels.Should().BeEquivalentTo(["feat"]);
        result.Items[1].Identifier.Should().Be("2");
        result.Items[1].Labels.Should().BeEmpty();
        result.Page.Should().Be(1);
        result.PageSize.Should().Be(25);
        result.HasMore.Should().BeFalse();
    }

    [Fact]
    public async Task ListOpenIssuesAsync_HasMore_WhenExtraItemReturned()
    {
        // Request pageSize=2, return 3 items → HasMore=true, only 2 items returned
        var issues = new List<Issue>
        {
            CreateOctokitIssue(1, "First", body: "b", labels: []),
            CreateOctokitIssue(2, "Second", body: "b", labels: []),
            CreateOctokitIssue(3, "Third", body: "b", labels: [])
        };

        _mockIssues
            .Setup(i => i.GetAllForRepository("owner", "repo", It.IsAny<RepositoryIssueRequest>(), It.IsAny<ApiOptions>()))
            .ReturnsAsync(issues.AsReadOnly());

        var result = await _provider.ListOpenIssuesAsync(1, 2, CancellationToken.None);

        result.Items.Should().HaveCount(2);
        result.HasMore.Should().BeTrue();
    }

    [Fact]
    public async Task ListOpenIssuesAsync_PassesCorrectApiOptions()
    {
        _mockIssues
            .Setup(i => i.GetAllForRepository("owner", "repo", It.IsAny<RepositoryIssueRequest>(), It.IsAny<ApiOptions>()))
            .ReturnsAsync(new List<Issue>().AsReadOnly());

        await _provider.ListOpenIssuesAsync(3, 10, CancellationToken.None);

        _mockIssues.Verify(i => i.GetAllForRepository("owner", "repo",
            It.IsAny<RepositoryIssueRequest>(),
            It.Is<ApiOptions>(o => o.StartPage == 3 && o.PageSize == 11 && o.PageCount == 1)),
            Times.Once);
    }

    [Fact]
    public async Task ListOpenIssuesAsync_InvalidPage_ThrowsArgumentOutOfRangeException()
    {
        var act = () => _provider.ListOpenIssuesAsync(0, 10, CancellationToken.None);
        await act.Should().ThrowAsync<ArgumentOutOfRangeException>();
    }

    [Fact]
    public async Task ListOpenIssuesAsync_InvalidPageSize_ThrowsArgumentOutOfRangeException()
    {
        var act = () => _provider.ListOpenIssuesAsync(1, 0, CancellationToken.None);
        await act.Should().ThrowAsync<ArgumentOutOfRangeException>();
    }

    [Fact]
    public async Task ListOpenIssuesAsync_PageSizeExceedsMax_ThrowsArgumentOutOfRangeException()
    {
        var act = () => _provider.ListOpenIssuesAsync(1, 101, CancellationToken.None);
        await act.Should().ThrowAsync<ArgumentOutOfRangeException>();
    }

    [Fact]
    public async Task GetIssueAsync_InvalidIdentifier_ThrowsArgumentException()
    {
        var act = () => _provider.GetIssueAsync("not-a-number", CancellationToken.None);
        await act.Should().ThrowAsync<ArgumentException>().WithMessage("*Invalid issue identifier*");
    }

    [Fact]
    public async Task PostCommentAsync_CallsOctokitCreateComment()
    {
        var mockComments = new Mock<IIssueCommentsClient>();
        _mockIssues.Setup(i => i.Comment).Returns(mockComments.Object);

        await _provider.PostCommentAsync("42", "Test comment body", CancellationToken.None);

        mockComments.Verify(c => c.Create("owner", "repo", 42, "Test comment body"), Times.Once);
    }

    [Fact]
    public async Task PostCommentAsync_InvalidIdentifier_ThrowsArgumentException()
    {
        var act = () => _provider.PostCommentAsync("not-a-number", "body", CancellationToken.None);
        await act.Should().ThrowAsync<ArgumentException>().WithMessage("*Invalid issue identifier*");
    }

    private static Issue CreateOctokitIssue(int number, string title, string? body, string[] labels)
    {
        var labelObjects = labels.Select(name =>
            new Label(0, string.Empty, name, "000000", string.Empty, "description", false)).ToList();

        return new Issue(
            url: string.Empty,
            htmlUrl: string.Empty,
            commentsUrl: string.Empty,
            eventsUrl: string.Empty,
            number: number,
            state: ItemState.Open,
            title: title,
            body: body,
            closedBy: null,
            user: null,
            labels: labelObjects.AsReadOnly(),
            assignee: null,
            assignees: null,
            milestone: null,
            comments: 0,
            pullRequest: null,
            closedAt: null,
            createdAt: DateTimeOffset.UtcNow,
            updatedAt: null,
            id: number,
            nodeId: string.Empty,
            locked: false,
            repository: null,
            reactions: null,
            activeLockReason: null,
            stateReason: null
        );
    }
}
