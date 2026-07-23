using AwesomeAssertions;
using Moq;
using Octokit;
using CodingAgentWebUI.Infrastructure.GitHub;
using CodingAgentWebUI.Infrastructure.Persistence;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Infrastructure;
using CodingAgentWebUI.Pipeline.Interfaces;

namespace CodingAgentWebUI.Infrastructure.UnitTests;

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
        _provider = new GitHubIssueProvider(new GitHubConnectionInfo("https://api.github.com", "owner", "repo"), _mockClient.Object);
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
    public async Task PostCommentAsync_CallsOctokitCreateComment_ReturnsHtmlUrl()
    {
        var mockComments = new Mock<IIssueCommentsClient>();
        mockComments.Setup(c => c.Create("owner", "repo", 42, "Test comment body"))
            .ReturnsAsync((Octokit.IssueComment)null!);
        _mockIssues.Setup(i => i.Comment).Returns(mockComments.Object);

        var url = await _provider.PostCommentAsync("42", "Test comment body", CancellationToken.None);

        mockComments.Verify(c => c.Create("owner", "repo", 42, "Test comment body"), Times.Once);
        // Returns null when Octokit returns null (URL extraction is null-safe)
        url.Should().BeNull();
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
    [InlineData("42", null, "body", "commentId")]
    [InlineData("42", "100", null, "body")]
    public async Task UpdateCommentAsync_NullParams_ThrowsArgumentNullException(
        string? issueIdentifier, string? commentId, string? body, string expectedParamName)
    {
        var act = () => _provider.UpdateCommentAsync(issueIdentifier!, commentId!, body!, CancellationToken.None);
        (await act.Should().ThrowAsync<ArgumentNullException>())
            .Which.ParamName.Should().Be(expectedParamName);
    }

    [Fact]
    public async Task UpdateCommentAsync_EmptyIdentifier_ThrowsArgumentException()
    {
        var act = () => _provider.UpdateCommentAsync(default(IssueIdentifier), "100", "body", CancellationToken.None);
        (await act.Should().ThrowAsync<ArgumentException>())
            .Which.ParamName.Should().Be("issueIdentifier.Value");
    }

    [Theory]
    [InlineData("not-a-number", "100", "identifier")]
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
    public async Task AddLabelsAsync_NullIdentifier_ThrowsArgumentException()
    {
        var act = () => _provider.AddLabelsAsync(default, new List<string> { "bug" }.AsReadOnly(), CancellationToken.None);
        (await act.Should().ThrowAsync<ArgumentException>())
            .Which.ParamName.Should().Be("identifier.Value");
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
    public async Task CloseIssueAsync_NullIdentifier_ThrowsArgumentException()
    {
        var act = () => _provider.CloseIssueAsync(default, CancellationToken.None);
        (await act.Should().ThrowAsync<ArgumentException>())
            .Which.ParamName.Should().Be("identifier.Value");
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
    public async Task RemoveLabelAsync_NullIdentifier_ThrowsArgumentException()
    {
        var act = () => _provider.RemoveLabelAsync(default, "label", CancellationToken.None);
        (await act.Should().ThrowAsync<ArgumentException>())
            .Which.ParamName.Should().Be("identifier.Value");
    }

    [Fact]
    public async Task RemoveLabelAsync_NonNumericIdentifier_ThrowsArgumentException()
    {
        var act = () => _provider.RemoveLabelAsync("abc", "label", CancellationToken.None);
        (await act.Should().ThrowAsync<ArgumentException>())
            .Which.ParamName.Should().Be("identifier");
    }

    [Fact]
    public async Task EnsureAgentLabelsAsync_CreatesAllSevenLabels()
    {
        var mockLabels = new Mock<IIssuesLabelsClient>();
        _mockIssues.Setup(i => i.Labels).Returns(mockLabels.Object);

        var result = await _provider.EnsureAgentLabelsAsync(CancellationToken.None);

        result.Should().BeTrue();
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
        mockLabels.Verify(l => l.Create("owner", "repo",
            It.Is<NewLabel>(nl => nl.Name == "agent:cancelled" && nl.Color == "c5def5")), Times.Once);
        mockLabels.Verify(l => l.Create("owner", "repo",
            It.Is<NewLabel>(nl => nl.Name == "agent:done" && nl.Color == "0075ca")), Times.Once);
    }

    [Fact]
    public async Task EnsureAgentLabelsAsync_SkipsExistingLabels()
    {
        var mockLabels = new Mock<IIssuesLabelsClient>();
        mockLabels.Setup(l => l.Create("owner", "repo", It.IsAny<NewLabel>()))
            .ThrowsAsync(new ApiValidationException());
        _mockIssues.Setup(i => i.Labels).Returns(mockLabels.Object);

        var result = await _provider.EnsureAgentLabelsAsync(CancellationToken.None);

        result.Should().BeTrue();
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

        var result = await _provider.EnsureAgentLabelsAsync(CancellationToken.None);

        result.Should().BeTrue();
        // All eleven should be attempted (7 agent labels + 1 consolidation label + 3 epic decomposition labels)
        mockLabels.Verify(l => l.Create("owner", "repo", It.IsAny<NewLabel>()), Times.Exactly(11));
    }

    [Fact]
    public async Task EnsureAgentLabelsAsync_ReturnsFalse_WhenLabelCreationFails()
    {
        var mockLabels = new Mock<IIssuesLabelsClient>();
        // Use NotFoundException (not retried by resilience pipeline) to avoid exponential backoff delays.
        mockLabels.Setup(l => l.Create("owner", "repo", It.Is<NewLabel>(nl => nl.Name == "agent:next")))
            .ThrowsAsync(new NotFoundException("Not found", System.Net.HttpStatusCode.NotFound));
        _mockIssues.Setup(i => i.Labels).Returns(mockLabels.Object);

        var result = await _provider.EnsureAgentLabelsAsync(CancellationToken.None);

        result.Should().BeFalse();
    }

    [Fact]
    public async Task ValidateAsync_Success_DoesNotThrow()
    {
        var mockRepos = new Mock<IRepositoriesClient>();
        mockRepos.Setup(r => r.Get("owner", "repo")).ReturnsAsync(CreateMockRepository());
        _mockClient.Setup(c => c.Repository).Returns(mockRepos.Object);

        await _provider.Invoking(p => p.ValidateAsync(CancellationToken.None)).Should().NotThrowAsync();
    }

    [Fact]
    public async Task ValidateAsync_AuthorizationException_ThrowsUserFriendlyMessage()
    {
        var mockRepos = new Mock<IRepositoriesClient>();
        mockRepos.Setup(r => r.Get("owner", "repo")).ThrowsAsync(new AuthorizationException());
        _mockClient.Setup(c => c.Repository).Returns(mockRepos.Object);

        var act = () => _provider.ValidateAsync(CancellationToken.None);
        var ex = await act.Should().ThrowAsync<InvalidOperationException>();
        ex.Which.Message.Should().Contain("Authentication failed: installation token was rejected");
    }

    [Fact]
    public async Task ValidateAsync_NotFoundException_ThrowsUserFriendlyMessage()
    {
        var mockRepos = new Mock<IRepositoriesClient>();
        mockRepos.Setup(r => r.Get("owner", "repo")).ThrowsAsync(new NotFoundException("Not found", System.Net.HttpStatusCode.NotFound));
        _mockClient.Setup(c => c.Repository).Returns(mockRepos.Object);

        var act = () => _provider.ValidateAsync(CancellationToken.None);
        var ex = await act.Should().ThrowAsync<InvalidOperationException>();
        ex.Which.Message.Should().Contain("Repository not found or app lacks access");
    }

    [Fact]
    public async Task ValidateAsync_PrivateKeyDecodeFailure_ThrowsUserFriendlyMessage()
    {
        var mockRepos = new Mock<IRepositoriesClient>();
        mockRepos.Setup(r => r.Get("owner", "repo")).ThrowsAsync(
            new GitHubAuthException(GitHubAuthErrorKind.PrivateKeyDecodeFailure, "Failed to decode private key from base64"));
        _mockClient.Setup(c => c.Repository).Returns(mockRepos.Object);

        var act = () => _provider.ValidateAsync(CancellationToken.None);
        var ex = await act.Should().ThrowAsync<InvalidOperationException>();
        ex.Which.Message.Should().Contain("Invalid private key: could not decode from base64");
    }

    [Fact]
    public async Task ValidateAsync_TokenExchangeFailure_ThrowsUserFriendlyMessage()
    {
        var mockRepos = new Mock<IRepositoriesClient>();
        mockRepos.Setup(r => r.Get("owner", "repo")).ThrowsAsync(
            new GitHubAuthException(GitHubAuthErrorKind.TokenExchangeFailure, "token exchange failed", new Exception("bad credentials")));
        _mockClient.Setup(c => c.Repository).Returns(mockRepos.Object);

        var act = () => _provider.ValidateAsync(CancellationToken.None);
        var ex = await act.Should().ThrowAsync<InvalidOperationException>();
        ex.Which.Message.Should().Contain("Authentication failed: bad credentials");
    }

    [Fact]
    public async Task InitializeAsync_Success_ReturnsTrue()
    {
        IIssueProvider provider = _provider;
        var mockRepos = new Mock<IRepositoriesClient>();
        mockRepos.Setup(r => r.Get("owner", "repo")).ReturnsAsync(CreateMockRepository());
        _mockClient.Setup(c => c.Repository).Returns(mockRepos.Object);
        var mockLabels = new Mock<IIssuesLabelsClient>();
        _mockIssues.Setup(i => i.Labels).Returns(mockLabels.Object);

        var result = await provider.InitializeAsync(CancellationToken.None);

        result.Should().BeTrue();
    }

    [Fact]
    public async Task InitializeAsync_LabelCreationFails_ReturnsFalse()
    {
        IIssueProvider provider = _provider;
        var mockRepos = new Mock<IRepositoriesClient>();
        mockRepos.Setup(r => r.Get("owner", "repo")).ReturnsAsync(CreateMockRepository());
        _mockClient.Setup(c => c.Repository).Returns(mockRepos.Object);
        var mockLabels = new Mock<IIssuesLabelsClient>();
        // Use NotFoundException (not retried by resilience pipeline) to avoid exponential backoff delays.
        // The test verifies graceful handling of label creation failure, not retry behavior.
        mockLabels.Setup(l => l.Create("owner", "repo", It.IsAny<NewLabel>()))
            .ThrowsAsync(new NotFoundException("Label endpoint not found", System.Net.HttpStatusCode.NotFound));
        _mockIssues.Setup(i => i.Labels).Returns(mockLabels.Object);

        var result = await provider.InitializeAsync(CancellationToken.None);

        result.Should().BeFalse();
    }

    [Fact]
    public async Task InitializeAsync_CredentialFailure_Throws()
    {
        IIssueProvider provider = _provider;
        var mockRepos = new Mock<IRepositoriesClient>();
        mockRepos.Setup(r => r.Get("owner", "repo")).ThrowsAsync(new AuthorizationException());
        _mockClient.Setup(c => c.Repository).Returns(mockRepos.Object);

        Func<Task> act = async () => await provider.InitializeAsync(CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*Authentication failed*");
    }

    [Fact]
    public async Task InitializeAsync_IsIdempotent_SecondCallSucceeds()
    {
        IIssueProvider provider = _provider;
        var mockRepos = new Mock<IRepositoriesClient>();
        mockRepos.Setup(r => r.Get("owner", "repo")).ReturnsAsync(CreateMockRepository());
        _mockClient.Setup(c => c.Repository).Returns(mockRepos.Object);
        var mockLabels = new Mock<IIssuesLabelsClient>();
        _mockIssues.Setup(i => i.Labels).Returns(mockLabels.Object);

        var result1 = await provider.InitializeAsync(CancellationToken.None);
        // Second call — labels already exist (ApiValidationException)
        mockLabels.Setup(l => l.Create("owner", "repo", It.IsAny<NewLabel>()))
            .ThrowsAsync(new ApiValidationException());
        var result2 = await provider.InitializeAsync(CancellationToken.None);

        result1.Should().BeTrue();
        result2.Should().BeTrue();
    }

    private static Repository CreateMockRepository()
    {
#pragma warning disable SYSLIB0050 // FormatterServices is obsolete — used only in tests to create uninitialized Octokit objects
        return (Repository)System.Runtime.Serialization.FormatterServices.GetUninitializedObject(typeof(Repository));
#pragma warning restore SYSLIB0050
    }

    [Fact]
    public async Task ListOpenIssuesAsync_RateLimitException_WrapsAsCustomException()
    {
        // Use a reset time in the past so the resilience pipeline's retry delay generator
        // falls through to default exponential backoff (~seconds) instead of waiting minutes.
        var resetTime = DateTimeOffset.UtcNow.AddSeconds(-1);
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

        var ex = await act.Should().ThrowAsync<CodingAgentWebUI.Pipeline.Models.RateLimitExceededException>();
        ex.Which.ResetAt.Should().BeCloseTo(resetTime, TimeSpan.FromSeconds(2));
        ex.Which.InnerException.Should().BeOfType<Octokit.RateLimitExceededException>();
    }

    // NOTE: [RES-03] Add tests for AbuseException wrapping — both RetryAfterSeconds.HasValue and fallback branches are untested (review finding #5)

    [Fact]
    public async Task ListClosedIssuesAsync_MapsIssuesToSummaries()
    {
        var issues = new List<Issue>
        {
            CreateOctokitIssue(10, "Closed One", body: "b", labels: ["agent:generated", "agent:done"]),
            CreateOctokitIssue(11, "Closed Two", body: "b", labels: ["agent:generated", "agent:wont-do"])
        };

        _mockIssues
            .Setup(i => i.GetAllForRepository("owner", "repo", It.IsAny<RepositoryIssueRequest>(), It.IsAny<ApiOptions>()))
            .ReturnsAsync(issues.AsReadOnly());

        var result = await _provider.ListClosedIssuesAsync(1, 25, null, null, CancellationToken.None);

        result.Items.Should().HaveCount(2);
        result.Items[0].Identifier.Should().Be("10");
        result.Items[0].Labels.Should().Contain("agent:done");
        result.Items[1].Identifier.Should().Be("11");
        result.Page.Should().Be(1);
        result.PageSize.Should().Be(25);
        result.HasMore.Should().BeFalse();
    }

    [Fact]
    public async Task ListClosedIssuesAsync_PassesClosedStateAndSince()
    {
        var since = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        _mockIssues
            .Setup(i => i.GetAllForRepository("owner", "repo",
                It.Is<RepositoryIssueRequest>(r => r.State == ItemStateFilter.Closed && r.Since == since),
                It.Is<ApiOptions>(o => o.StartPage == 1 && o.PageSize == 21 && o.PageCount == 1)))
            .ReturnsAsync(new List<Issue>().AsReadOnly());

        var result = await _provider.ListClosedIssuesAsync(1, 20, null, since, CancellationToken.None);

        result.Items.Should().BeEmpty();
        _mockIssues.Verify(i => i.GetAllForRepository("owner", "repo",
            It.Is<RepositoryIssueRequest>(r => r.State == ItemStateFilter.Closed && r.Since == since),
            It.Is<ApiOptions>(o => o.StartPage == 1 && o.PageSize == 21 && o.PageCount == 1)),
            Times.Once);
    }

    [Fact]
    public async Task ListClosedIssuesAsync_PassesLabelsToRequest()
    {
        RepositoryIssueRequest? capturedRequest = null;
        _mockIssues
            .Setup(i => i.GetAllForRepository("owner", "repo", It.IsAny<RepositoryIssueRequest>(), It.IsAny<ApiOptions>()))
            .Callback<string, string, RepositoryIssueRequest, ApiOptions>((_, _, req, _) => capturedRequest = req)
            .ReturnsAsync(new List<Issue>().AsReadOnly());

        var labels = new List<string> { "agent:generated" }.AsReadOnly();
        await _provider.ListClosedIssuesAsync(1, 20, labels, null, CancellationToken.None);

        capturedRequest.Should().NotBeNull();
        capturedRequest!.Labels.Should().BeEquivalentTo(labels);
    }

    [Fact]
    public async Task ListClosedIssuesAsync_FiltersPullRequests()
    {
        var pr = new PullRequest(0, string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, 0, ItemState.Closed, "title", "body", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, null, null, null, null, null, null, null, false, null, null, null, null, 0, 0, 0, 0, 0, null, false, null, null, null, null, null);
        var issueWithPr = new Issue(
            url: string.Empty, htmlUrl: string.Empty, commentsUrl: string.Empty, eventsUrl: string.Empty,
            number: 1, state: ItemState.Closed, title: "PR", body: null, closedBy: null, user: null,
            labels: new List<Label>().AsReadOnly(), assignee: null, assignees: null, milestone: null,
            comments: 0, pullRequest: pr, closedAt: null, createdAt: DateTimeOffset.UtcNow,
            updatedAt: null, id: 1, nodeId: string.Empty, locked: false, repository: null,
            reactions: null, activeLockReason: null, stateReason: null);
        var regularIssue = CreateOctokitIssue(2, "Real Issue", body: "b", labels: ["agent:generated"]);

        _mockIssues
            .Setup(i => i.GetAllForRepository("owner", "repo", It.IsAny<RepositoryIssueRequest>(), It.IsAny<ApiOptions>()))
            .ReturnsAsync(new List<Issue> { issueWithPr, regularIssue }.AsReadOnly());

        var result = await _provider.ListClosedIssuesAsync(1, 25, null, null, CancellationToken.None);

        result.Items.Should().HaveCount(1);
        result.Items[0].Identifier.Should().Be("2");
    }

    [Fact]
    public async Task ListClosedIssuesAsync_HasMore_WhenExtraItemReturned()
    {
        var issues = new List<Issue>
        {
            CreateOctokitIssue(1, "A", body: "b", labels: []),
            CreateOctokitIssue(2, "B", body: "b", labels: []),
            CreateOctokitIssue(3, "C", body: "b", labels: [])
        };

        _mockIssues
            .Setup(i => i.GetAllForRepository("owner", "repo", It.IsAny<RepositoryIssueRequest>(), It.IsAny<ApiOptions>()))
            .ReturnsAsync(issues.AsReadOnly());

        var result = await _provider.ListClosedIssuesAsync(1, 2, null, null, CancellationToken.None);

        result.Items.Should().HaveCount(2);
        result.HasMore.Should().BeTrue();
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
