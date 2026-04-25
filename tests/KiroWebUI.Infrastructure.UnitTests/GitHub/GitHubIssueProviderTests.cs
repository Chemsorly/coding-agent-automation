using AwesomeAssertions;
using Moq;
using Octokit;
using KiroWebUI.Infrastructure.GitHub;
using KiroWebUI.Infrastructure.Agent;
using KiroWebUI.Infrastructure.Persistence;
using KiroWebUI.Infrastructure;

namespace KiroWebUI.Infrastructure.UnitTests;

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

    [Fact]
    public async Task UpdateCommentAsync_CallsOctokitUpdateComment()
    {
        var mockComments = new Mock<IIssueCommentsClient>();
        _mockIssues.Setup(i => i.Comment).Returns(mockComments.Object);

        await _provider.UpdateCommentAsync("42", "100", "Updated body", CancellationToken.None);

        mockComments.Verify(c => c.Update("owner", "repo", 100, "Updated body"), Times.Once);
    }

    [Theory]
    [InlineData(null, "100", "body", "issueIdentifier")]
    [InlineData("42", null, "body", "commentId")]
    [InlineData("42", "100", null, "body")]
    public async Task UpdateCommentAsync_NullParams_ThrowsArgumentNullException(
        string? issueIdentifier, string? commentId, string? body, string expectedParamName)
    {
        var act = () => _provider.UpdateCommentAsync(issueIdentifier!, commentId!, body!, CancellationToken.None);
        (await act.Should().ThrowAsync<ArgumentNullException>())
            .Which.ParamName.Should().Be(expectedParamName);
    }

    [Theory]
    [InlineData("not-a-number", "100", "issueIdentifier")]
    [InlineData("42", "not-a-number", "commentId")]
    public async Task UpdateCommentAsync_NonNumericIdentifier_ThrowsArgumentException(
        string issueIdentifier, string commentId, string expectedParamName)
    {
        var act = () => _provider.UpdateCommentAsync(issueIdentifier, commentId, "body", CancellationToken.None);
        (await act.Should().ThrowAsync<ArgumentException>())
            .Which.ParamName.Should().Be(expectedParamName);
    }

    [Fact]
    public async Task AddLabelsAsync_CallsOctokitAddToIssue()
    {
        var mockLabels = new Mock<IIssuesLabelsClient>();
        _mockIssues.Setup(i => i.Labels).Returns(mockLabels.Object);

        var labels = new List<string> { "bug", "priority-high" }.AsReadOnly();
        await _provider.AddLabelsAsync("42", labels, CancellationToken.None);

        mockLabels.Verify(l => l.AddToIssue("owner", "repo", 42, It.Is<string[]>(a =>
            a.Length == 2 && a[0] == "bug" && a[1] == "priority-high")), Times.Once);
    }

    [Fact]
    public async Task AddLabelsAsync_NullIdentifier_ThrowsArgumentNullException()
    {
        var act = () => _provider.AddLabelsAsync(null!, new List<string> { "bug" }.AsReadOnly(), CancellationToken.None);
        (await act.Should().ThrowAsync<ArgumentNullException>())
            .Which.ParamName.Should().Be("identifier");
    }

    [Fact]
    public async Task AddLabelsAsync_NullLabels_ThrowsArgumentNullException()
    {
        var act = () => _provider.AddLabelsAsync("42", null!, CancellationToken.None);
        (await act.Should().ThrowAsync<ArgumentNullException>())
            .Which.ParamName.Should().Be("labels");
    }

    [Fact]
    public async Task AddLabelsAsync_NonNumericIdentifier_ThrowsArgumentException()
    {
        var act = () => _provider.AddLabelsAsync("not-a-number", new List<string> { "bug" }.AsReadOnly(), CancellationToken.None);
        (await act.Should().ThrowAsync<ArgumentException>())
            .Which.ParamName.Should().Be("identifier");
    }

    [Fact]
    public async Task CloseIssueAsync_CallsOctokitUpdateWithClosedState()
    {
        _mockIssues.Setup(i => i.Update("owner", "repo", 42, It.Is<IssueUpdate>(u => u.State == ItemState.Closed)))
            .ReturnsAsync(CreateOctokitIssue(42, "Test", body: null, labels: []));

        await _provider.CloseIssueAsync("42", CancellationToken.None);

        _mockIssues.Verify(i => i.Update("owner", "repo", 42,
            It.Is<IssueUpdate>(u => u.State == ItemState.Closed)), Times.Once);
    }

    [Fact]
    public async Task CloseIssueAsync_NullIdentifier_ThrowsArgumentNullException()
    {
        var act = () => _provider.CloseIssueAsync(null!, CancellationToken.None);
        (await act.Should().ThrowAsync<ArgumentNullException>())
            .Which.ParamName.Should().Be("identifier");
    }

    [Fact]
    public async Task CloseIssueAsync_NonNumericIdentifier_ThrowsArgumentException()
    {
        var act = () => _provider.CloseIssueAsync("not-a-number", CancellationToken.None);
        (await act.Should().ThrowAsync<ArgumentException>())
            .Which.ParamName.Should().Be("identifier");
    }

    [Fact]
    public async Task RemoveLabelAsync_CallsOctokitRemoveFromIssue()
    {
        var mockLabels = new Mock<IIssuesLabelsClient>();
        _mockIssues.Setup(i => i.Labels).Returns(mockLabels.Object);

        await _provider.RemoveLabelAsync("42", "agent:in-progress", CancellationToken.None);

        mockLabels.Verify(l => l.RemoveFromIssue("owner", "repo", 42, "agent:in-progress"), Times.Once);
    }

    [Fact]
    public async Task RemoveLabelAsync_LabelNotPresent_DoesNotThrow()
    {
        var mockLabels = new Mock<IIssuesLabelsClient>();
        mockLabels.Setup(l => l.RemoveFromIssue("owner", "repo", 42, "agent:next"))
            .ThrowsAsync(new NotFoundException("Not found", System.Net.HttpStatusCode.NotFound));
        _mockIssues.Setup(i => i.Labels).Returns(mockLabels.Object);

        await _provider.RemoveLabelAsync("42", "agent:next", CancellationToken.None);
    }

    [Fact]
    public async Task RemoveLabelAsync_NullIdentifier_ThrowsArgumentNullException()
    {
        var act = () => _provider.RemoveLabelAsync(null!, "label", CancellationToken.None);
        (await act.Should().ThrowAsync<ArgumentNullException>())
            .Which.ParamName.Should().Be("identifier");
    }

    [Fact]
    public async Task RemoveLabelAsync_NonNumericIdentifier_ThrowsArgumentException()
    {
        var act = () => _provider.RemoveLabelAsync("abc", "label", CancellationToken.None);
        (await act.Should().ThrowAsync<ArgumentException>())
            .Which.ParamName.Should().Be("identifier");
    }

    [Fact]
    public async Task EnsureAgentLabelsAsync_CreatesAllFourLabels()
    {
        var mockLabels = new Mock<IIssuesLabelsClient>();
        _mockIssues.Setup(i => i.Labels).Returns(mockLabels.Object);

        await _provider.EnsureAgentLabelsAsync(CancellationToken.None);

        mockLabels.Verify(l => l.Create("owner", "repo",
            It.Is<NewLabel>(nl => nl.Name == "agent:next" && nl.Color == "0e8a16")), Times.Once);
        mockLabels.Verify(l => l.Create("owner", "repo",
            It.Is<NewLabel>(nl => nl.Name == "agent:in-progress" && nl.Color == "1d76db")), Times.Once);
        mockLabels.Verify(l => l.Create("owner", "repo",
            It.Is<NewLabel>(nl => nl.Name == "agent:error" && nl.Color == "d73a4a")), Times.Once);
        mockLabels.Verify(l => l.Create("owner", "repo",
            It.Is<NewLabel>(nl => nl.Name == "agent:needs-refinement" && nl.Color == "fbca04")), Times.Once);
        mockLabels.Verify(l => l.Create("owner", "repo",
            It.Is<NewLabel>(nl => nl.Name == "agent:wont-do" && nl.Color == "cfd3d7")), Times.Once);
    }

    [Fact]
    public async Task EnsureAgentLabelsAsync_SkipsExistingLabels()
    {
        var mockLabels = new Mock<IIssuesLabelsClient>();
        mockLabels.Setup(l => l.Create("owner", "repo", It.IsAny<NewLabel>()))
            .ThrowsAsync(new ApiValidationException());
        _mockIssues.Setup(i => i.Labels).Returns(mockLabels.Object);

        // Should not throw — all labels already exist
        await _provider.EnsureAgentLabelsAsync(CancellationToken.None);
    }

    [Fact]
    public async Task EnsureAgentLabelsAsync_CreatesOnlyMissingLabels()
    {
        var mockLabels = new Mock<IIssuesLabelsClient>();
        // First two labels already exist, rest are new
        mockLabels.Setup(l => l.Create("owner", "repo", It.Is<NewLabel>(nl => nl.Name == "agent:next")))
            .ThrowsAsync(new ApiValidationException());
        mockLabels.Setup(l => l.Create("owner", "repo", It.Is<NewLabel>(nl => nl.Name == "agent:in-progress")))
            .ThrowsAsync(new ApiValidationException());
        _mockIssues.Setup(i => i.Labels).Returns(mockLabels.Object);

        await _provider.EnsureAgentLabelsAsync(CancellationToken.None);

        // All five should be attempted
        mockLabels.Verify(l => l.Create("owner", "repo", It.IsAny<NewLabel>()), Times.Exactly(5));
    }

    [Fact]
    public async Task ListOpenIssuesAsync_RateLimitException_WrapsAsCustomException()
    {
        var resetTime = DateTimeOffset.UtcNow.AddMinutes(5);
        var resetUnix = resetTime.ToUnixTimeSeconds().ToString();
        var headers = new Dictionary<string, string>
        {
            { "X-RateLimit-Limit", "5000" },
            { "X-RateLimit-Remaining", "0" },
            { "X-RateLimit-Reset", resetUnix }
        };
        var rateLimit = new RateLimit(5000, 0, resetTime.ToUnixTimeSeconds());
        var apiInfo = new ApiInfo(new Dictionary<string, Uri>(), new List<string>(), new List<string>(),
            string.Empty, rateLimit);
        var response = new Mock<Octokit.IResponse>();
        response.Setup(r => r.StatusCode).Returns(System.Net.HttpStatusCode.Forbidden);
        response.Setup(r => r.Headers).Returns(headers);
        response.Setup(r => r.Body).Returns("");
        response.Setup(r => r.ContentType).Returns("application/json");
        response.Setup(r => r.ApiInfo).Returns(apiInfo);

        _mockIssues.Setup(i => i.GetAllForRepository("owner", "repo",
                It.IsAny<RepositoryIssueRequest>(), It.IsAny<ApiOptions>()))
            .ThrowsAsync(new Octokit.RateLimitExceededException(response.Object));

        var act = () => _provider.ListOpenIssuesAsync(1, 10, CancellationToken.None);

        var ex = await act.Should().ThrowAsync<KiroWebUI.Pipeline.Models.RateLimitExceededException>();
        ex.Which.ResetAt.Should().BeCloseTo(resetTime, TimeSpan.FromSeconds(2));
        ex.Which.InnerException.Should().BeOfType<Octokit.RateLimitExceededException>();
    }

    // TODO: [RES-03] Add tests for AbuseException wrapping — both RetryAfterSeconds.HasValue and fallback branches are untested (review finding #5)

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
