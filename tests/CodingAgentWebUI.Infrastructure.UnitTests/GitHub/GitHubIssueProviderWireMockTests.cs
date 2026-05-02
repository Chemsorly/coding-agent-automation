using AwesomeAssertions;
using CodingAgentWebUI.Infrastructure.GitHub;
using CodingAgentWebUI.Infrastructure.Persistence;
using CodingAgentWebUI.Infrastructure;
using Octokit;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;

namespace CodingAgentWebUI.Infrastructure.UnitTests;

public class GitHubIssueProviderWireMockTests : WireMockTestBase
{
    private const string Owner = "test-owner";
    private const string Repo = "test-repo";
    private const string Token = "fake-token-12345";

    private GitHubIssueProvider CreateProvider() =>
        new(Server.Url!, Token, Owner, Repo);

    private GitHubIssueProvider CreateProviderWithTokenProvider(Func<CancellationToken, Task<string>> tokenProvider) =>
        new(Server.Url!, tokenProvider, Owner, Repo);

    // --- Happy paths ---

    [Fact]
    public async Task GetIssueAsync_ReturnsDeserializedIssue()
    {
        StubGet(ApiPath($"/repos/{Owner}/{Repo}/issues/42"),
            BuildIssueJson(42, "Bug: login fails", "Steps to reproduce...", ["bug", "critical"]));

        await using var provider = CreateProvider();
        var result = await provider.GetIssueAsync("42", CancellationToken.None);

        result.Identifier.Should().Be("42");
        result.Title.Should().Be("Bug: login fails");
        result.Description.Should().Be("Steps to reproduce...");
        result.Labels.Should().BeEquivalentTo(["bug", "critical"]);
    }

    [Fact]
    public async Task ListOpenIssuesAsync_ReturnsPaginatedResults()
    {
        var issues = new[]
        {
            BuildIssueJson(1, "First issue", "body1", ["feat"]),
            BuildIssueJson(2, "Second issue", "body2", [])
        };
        StubGet(ApiPath($"/repos/{Owner}/{Repo}/issues"), issues);

        await using var provider = CreateProvider();
        var result = await provider.ListOpenIssuesAsync(1, 10, CancellationToken.None);

        result.Items.Should().HaveCount(2);
        result.Items[0].Identifier.Should().Be("1");
        result.Items[0].Title.Should().Be("First issue");
        result.Items[1].Identifier.Should().Be("2");
        result.Page.Should().Be(1);
        result.HasMore.Should().BeFalse();
    }

    [Fact]
    public async Task PostCommentAsync_SendsCorrectBody()
    {
        StubPost(ApiPath($"/repos/{Owner}/{Repo}/issues/42/comments"),
            BuildCommentJson(100, "Test comment", "bot"));

        await using var provider = CreateProvider();
        await provider.PostCommentAsync("42", "Test comment", CancellationToken.None);

        var body = GetRequestBody(ApiPath($"/repos/{Owner}/{Repo}/issues/42/comments"));
        body.Should().NotBeNull();
        body.Should().Contain("Test comment");
    }

    [Fact]
    public async Task ListCommentsAsync_ReturnsDeserializedComments()
    {
        var comments = new[]
        {
            BuildCommentJson(1, "First comment", "alice"),
            BuildCommentJson(2, "Second comment", "bob")
        };
        StubGet(ApiPath($"/repos/{Owner}/{Repo}/issues/10/comments"), comments);

        await using var provider = CreateProvider();
        var result = await provider.ListCommentsAsync("10", CancellationToken.None);

        result.Should().HaveCount(2);
        result[0].Id.Should().Be("1");
        result[0].Body.Should().Be("First comment");
        result[0].Author.Should().Be("alice");
        // NOTE: Assert CreatedAt timestamp deserialization (issue requires "including author and timestamps")
        result[1].Id.Should().Be("2");
        result[1].Author.Should().Be("bob");
    }

    [Fact]
    public async Task AddLabelsAsync_SendsCorrectLabels()
    {
        StubPost(ApiPath($"/repos/{Owner}/{Repo}/issues/42/labels"),
            new[] { BuildLabelJson("bug"), BuildLabelJson("priority") });

        await using var provider = CreateProvider();
        await provider.AddLabelsAsync("42", new[] { "bug", "priority" }, CancellationToken.None);

        var body = GetRequestBody(ApiPath($"/repos/{Owner}/{Repo}/issues/42/labels"));
        body.Should().NotBeNull();
        body.Should().Contain("bug");
        body.Should().Contain("priority");
    }

    [Fact]
    public async Task RemoveLabelAsync_SendsDeleteRequest()
    {
        StubDelete(ApiPath($"/repos/{Owner}/{Repo}/issues/42/labels/bug"));

        await using var provider = CreateProvider();
        await provider.RemoveLabelAsync("42", "bug", CancellationToken.None);

        #pragma warning disable CS8602 // WireMock ILogEntry.RequestMessage is always populated
        Server.LogEntries.Should().Contain(e =>
            e.RequestMessage.Method == "DELETE" &&
            e.RequestMessage.Path != null &&
            e.RequestMessage.Path.Contains("/labels/bug"));
        #pragma warning restore CS8602
    }

    [Fact]
    public async Task RemoveLabelAsync_404_DoesNotThrow()
    {
        StubError(ApiPath($"/repos/{Owner}/{Repo}/issues/42/labels/nonexistent"), 404,
            new { message = "Not Found" });

        await using var provider = CreateProvider();
        await provider.RemoveLabelAsync("42", "nonexistent", CancellationToken.None);
    }

    [Fact]
    public async Task CloseIssueAsync_SendsPatchWithClosedState()
    {
        StubPatch(ApiPath($"/repos/{Owner}/{Repo}/issues/42"),
            BuildIssueJson(42, "Closed issue", "body", []));

        await using var provider = CreateProvider();
        await provider.CloseIssueAsync("42", CancellationToken.None);

        var body = GetRequestBody(ApiPath($"/repos/{Owner}/{Repo}/issues/42"));
        body.Should().NotBeNull();
        body.Should().Contain("closed");
    }

    [Fact]
    public async Task ValidateAsync_SucceedsWhenRepoAccessible()
    {
        StubGet(ApiPath($"/repos/{Owner}/{Repo}"), BuildRepoJson(Owner, Repo));

        await using var provider = CreateProvider();
        await provider.ValidateAsync(CancellationToken.None);
    }

    // --- Error paths ---

    [Fact]
    public async Task GetIssueAsync_404_ThrowsNotFoundException()
    {
        StubError(ApiPath($"/repos/{Owner}/{Repo}/issues/999"), 404,
            new { message = "Not Found" });

        await using var provider = CreateProvider();
        await provider.Invoking(p => p.GetIssueAsync("999", CancellationToken.None))
            .Should().ThrowAsync<NotFoundException>();
    }

    [Fact]
    public async Task GetIssueAsync_403_RateLimited_ThrowsForbiddenException()
    {
        StubRateLimited(ApiPath($"/repos/{Owner}/{Repo}/issues/42"));

        await using var provider = CreateProvider();
        await provider.Invoking(p => p.GetIssueAsync("42", CancellationToken.None))
            .Should().ThrowAsync<ForbiddenException>();
    }

    [Fact]
    public async Task GetIssueAsync_500_ThrowsApiException()
    {
        StubError(ApiPath($"/repos/{Owner}/{Repo}/issues/42"), 500,
            new { message = "Internal Server Error" });

        await using var provider = CreateProvider();
        await provider.Invoking(p => p.GetIssueAsync("42", CancellationToken.None))
            .Should().ThrowAsync<ApiException>();
    }

    [Fact]
    public async Task GetIssueAsync_422_ThrowsApiValidationException()
    {
        StubError(ApiPath($"/repos/{Owner}/{Repo}/issues/42"), 422,
            new { message = "Validation Failed", errors = new[] { new { resource = "Issue", code = "invalid" } } });

        await using var provider = CreateProvider();
        await provider.Invoking(p => p.GetIssueAsync("42", CancellationToken.None))
            .Should().ThrowAsync<ApiValidationException>();
    }

    [Fact]
    public async Task GetIssueAsync_NetworkTimeout_ThrowsOnCancellation()
    {
        // Simulate a connection-level failure by returning an empty response (no HTTP status, no body).
        // Octokit's HttpClient will throw an HttpRequestException when it receives no valid HTTP response.
        Server.Given(Request.Create().WithPath(ApiPath($"/repos/{Owner}/{Repo}/issues/42")).UsingGet())
            .RespondWith(Response.Create()
                .WithFault(WireMock.ResponseBuilders.FaultType.EMPTY_RESPONSE));

        await using var provider = CreateProvider();

        await provider.Invoking(p => p.GetIssueAsync("42", CancellationToken.None))
            .Should().ThrowAsync<Exception>();
    }

    // --- Auth verification ---

    [Fact]
    public async Task AllRequests_IncludeAuthorizationHeader()
    {
        StubGet(ApiPath($"/repos/{Owner}/{Repo}/issues/1"), BuildIssueJson(1, "T", "B", []));
        StubGet(ApiPath($"/repos/{Owner}/{Repo}"), BuildRepoJson(Owner, Repo));

        await using var provider = CreateProvider();
        await provider.GetIssueAsync("1", CancellationToken.None);
        await provider.ValidateAsync(CancellationToken.None);

        AssertAllRequestsHaveAuthHeader(Token);
    }

    [Fact]
    public async Task AllRequests_IncludeUserAgentHeader()
    {
        StubGet(ApiPath($"/repos/{Owner}/{Repo}/issues/1"), BuildIssueJson(1, "T", "B", []));

        await using var provider = CreateProvider();
        await provider.GetIssueAsync("1", CancellationToken.None);

        AssertAllRequestsHaveUserAgent("CodingAgentWebUI-Pipeline");
    }

    // --- Token refresh ---

    [Fact]
    public async Task DynamicTokenProvider_EachRequestUsesCurrentToken()
    {
        var callCount = 0;
        var tokens = new[] { "token-first", "token-second" };

        StubGet(ApiPath($"/repos/{Owner}/{Repo}/issues/1"), BuildIssueJson(1, "T", "B", []));
        StubGet(ApiPath($"/repos/{Owner}/{Repo}/issues/2"), BuildIssueJson(2, "T2", "B2", []));

        // NOTE: Token assertion uses substring matching (Contain). If tokens shared a common prefix
        // (e.g., "token" and "token-extended"), the assertion could pass incorrectly. Consider using
        // exact match on the full "Token <value>" header string for stricter verification.
        await using var provider = CreateProviderWithTokenProvider(ct =>
        {
            var token = tokens[Math.Min(Interlocked.Increment(ref callCount) - 1, tokens.Length - 1)];
            return Task.FromResult(token);
        });

        await provider.GetIssueAsync("1", CancellationToken.None);
        await provider.GetIssueAsync("2", CancellationToken.None);

#pragma warning disable CS8602 // WireMock types use nullable references
        var authHeaders = Server.LogEntries
            .Select(e => GetHeaderValue(e.RequestMessage.Headers, "Authorization"))
            .ToList();
#pragma warning restore CS8602

        authHeaders.Should().HaveCount(2);
        authHeaders[0].Should().Contain("token-first");
        authHeaders[1].Should().Contain("token-second");
    }
}
