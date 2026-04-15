using FluentAssertions;
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
            .Setup(i => i.GetAllForRepository("owner", "repo", It.IsAny<RepositoryIssueRequest>()))
            .ReturnsAsync(issues.AsReadOnly());

        var result = await _provider.ListOpenIssuesAsync(CancellationToken.None);

        result.Should().HaveCount(2);
        result[0].Identifier.Should().Be("1");
        result[0].Title.Should().Be("First");
        result[0].Labels.Should().BeEquivalentTo(["feat"]);
        result[1].Identifier.Should().Be("2");
        result[1].Labels.Should().BeEmpty();
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
