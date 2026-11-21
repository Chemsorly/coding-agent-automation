using AwesomeAssertions;
using CodingAgent.Infrastructure.GitHub;
using CodingAgent.Infrastructure.Persistence;
using CodingAgent.Infrastructure;
using Octokit;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;

namespace CodingAgent.Infrastructure.UnitTests;

[Collection("GitHubTelemetry")]
public class GitHubIssueProviderWireMockTests : WireMockTestBase
{
    private const string Owner = "test-owner";
    private const string Repo = "test-repo";
    private const string Token = "fake-token-12345";

    private GitHubIssueProvider CreateProvider() =>
        new(new GitHubConnectionInfo(Server.Url!, Owner, Repo), Token);

    private GitHubIssueProvider CreateProviderWithTokenProvider(Func<CancellationToken, Task<string>> tokenProvider) =>
        new(new GitHubConnectionInfo(Server.Url!, Owner, Repo), tokenProvider);

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
        var act = () => provider.RemoveLabelAsync("42", "nonexistent", CancellationToken.None);
        await act.Should().NotThrowAsync();
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

        Server.LogEntries.Should().NotBeEmpty("at least one request should have been made to the stub server");
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
            .Should().ThrowAsync<CodingAgent.Pipeline.Models.RateLimitExceededException>();
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

        AssertAllRequestsHaveUserAgent("CodingAgent.Web-Pipeline");
    }

    // --- Token refresh ---

    [Fact]
    public async Task DynamicTokenProvider_EachRequestUsesCurrentToken()
    {
        var callCount = 0;
        var tokens = new[] { "token-first", "token-second" };

        StubGet(ApiPath($"/repos/{Owner}/{Repo}/issues/1"), BuildIssueJson(1, "T", "B", []));
        StubGet(ApiPath($"/repos/{Owner}/{Repo}/issues/2"), BuildIssueJson(2, "T2", "B2", []));

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

        // Each request delegates to the token provider (no local caching — upstream GitHubAppAuthService caches)
        authHeaders.Should().HaveCount(2);
        authHeaders[0].Should().Contain("token-first");
        authHeaders[1].Should().Contain("token-second");
        callCount.Should().Be(2, "token provider is called per request (caching is upstream)");
    }

    // --- Issue reads: mapping, paging and query ---

    private static string IssuesPath => ApiPath($"/repos/{Owner}/{Repo}/issues");

    /// <summary>A pull request as the Issues API lists it: an issue with a <c>pull_request</c> member.</summary>
    private static object BuildPullRequestIssueJson(int number, string title) => new
    {
        id = number * 100,
        number,
        title,
        state = "open",
        user = new { login = "testuser", id = 1 },
        author_association = "MEMBER",
        labels = Array.Empty<object>(),
        created_at = "2026-01-01T00:00:00Z",
        updated_at = "2026-01-01T00:00:00Z",
        pull_request = new { url = "https://example.test/pr" }
    };

    /// <summary>The decoded query of the one list request the provider sent to the issues path.</summary>
    private Dictionary<string, string> ListRequestQuery()
    {
        var request = Server.LogEntries.Single(e => e.RequestMessage?.Path == IssuesPath).RequestMessage!;
        return (request.RawQuery ?? string.Empty)
            .TrimStart('?')
            .Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(pair => pair.Split('=', 2))
            .ToDictionary(
                pair => Uri.UnescapeDataString(pair[0]),
                pair => pair.Length > 1 ? Uri.UnescapeDataString(pair[1]) : string.Empty);
    }

    [Fact]
    public async Task GetIssueAsync_WithMissingBody_ReturnsEmptyDescription()
    {
        StubGet(ApiPath($"/repos/{Owner}/{Repo}/issues/42"), BuildIssueJson(42, "Test Issue", null, []));

        await using var provider = CreateProvider();
        var result = await provider.GetIssueAsync("42", CancellationToken.None);

        result.Description.Should().BeEmpty();
        result.Title.Should().Be("Test Issue");
        result.Identifier.Should().Be("42");
    }

    [Fact]
    public async Task GetIssueAsync_MapsLabelsCorrectly()
    {
        var labels = new[] { "bug", "priority-high", "backend" };
        StubGet(ApiPath($"/repos/{Owner}/{Repo}/issues/10"), BuildIssueJson(10, "Bug Fix", "Fix the thing", labels));

        await using var provider = CreateProvider();
        var result = await provider.GetIssueAsync("10", CancellationToken.None);

        result.Labels.Should().BeEquivalentTo(labels);
    }

    [Fact]
    public async Task ListOpenIssuesAsync_MapsIssuesToSummaries()
    {
        StubGet(IssuesPath, new[]
        {
            BuildIssueJson(1, "First", "body1", ["feat"]),
            BuildIssueJson(2, "Second", "body2", [])
        });

        await using var provider = CreateProvider();
        var result = await provider.ListOpenIssuesAsync(1, 25, CancellationToken.None);

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
    public async Task ListOpenIssuesAsync_MapsDescriptionLabelColorsUrlAndCreatedAt()
    {
        StubGet(IssuesPath, new[]
        {
            new
            {
                number = 7,
                title = "Scan issue",
                body = "## Problem\nBody text",
                html_url = "https://example.test/issues/7",
                author_association = "MEMBER",
                user = new { login = "testuser", type = "User" },
                labels = new[] { new { name = "feat", color = "a2eeef" } },
                created_at = "2026-02-03T04:05:06Z"
            }
        });

        await using var provider = CreateProvider();
        var result = await provider.ListOpenIssuesAsync(1, 25, CancellationToken.None);

        var item = result.Items.Should().ContainSingle().Subject;
        item.Description.Should().Be("## Problem\nBody text");
        item.LabelColors.Should().ContainKey("feat").WhoseValue.Should().Be("a2eeef");
        item.CreatedAt.Should().Be(new DateTime(2026, 2, 3, 4, 5, 6, DateTimeKind.Utc));
        item.Url.Should().Be("https://example.test/issues/7");
    }

    [Fact]
    public async Task ListOpenIssuesAsync_LabelWithNullName_MapsToEmptyStringWithoutThrowing()
    {
        StubGet(IssuesPath, new[]
        {
            new
            {
                number = 1,
                title = "Odd label",
                body = "b",
                author_association = "MEMBER",
                user = new { login = "testuser", type = "User" },
                labels = new[] { new { name = (string?)null, color = "ededed" } },
                created_at = "2026-01-01T00:00:00Z"
            }
        });

        await using var provider = CreateProvider();
        var result = await provider.ListOpenIssuesAsync(1, 25, CancellationToken.None);

        var item = result.Items.Should().ContainSingle().Subject;
        item.Labels.Should().Equal(string.Empty);
        item.LabelColors.Should().ContainKey(string.Empty).WhoseValue.Should().Be("ededed");
    }

    [Fact]
    public async Task ListOpenIssuesAsync_IssueWithoutLabelsMember_MapsToNoLabels()
    {
        StubGet(IssuesPath, new[]
        {
            new
            {
                number = 1,
                title = "No labels",
                body = "b",
                author_association = "MEMBER",
                user = new { login = "testuser", type = "User" },
                created_at = "2026-01-01T00:00:00Z"
            }
        });

        await using var provider = CreateProvider();
        var result = await provider.ListOpenIssuesAsync(1, 25, CancellationToken.None);

        var item = result.Items.Should().ContainSingle().Subject;
        item.Labels.Should().BeEmpty();
        item.LabelColors.Should().BeNull();
    }

    [Fact]
    public async Task GetIssueAsync_LabelWithNullNameAndMissingLabelsMember_MapsWithoutThrowing()
    {
        StubGet(ApiPath($"/repos/{Owner}/{Repo}/issues/1"), new
        {
            number = 1,
            title = "Odd label",
            body = "b",
            author_association = "MEMBER",
            user = new { login = "testuser", type = "User" },
            labels = new[] { new { name = (string?)null, color = "ededed" } },
            created_at = "2026-01-01T00:00:00Z"
        });
        StubGet(ApiPath($"/repos/{Owner}/{Repo}/issues/2"), new
        {
            number = 2,
            title = "No labels",
            body = "b",
            author_association = "MEMBER",
            user = new { login = "testuser", type = "User" },
            created_at = "2026-01-01T00:00:00Z"
        });

        await using var provider = CreateProvider();
        var withNullName = await provider.GetIssueAsync("1", CancellationToken.None);
        var withoutLabels = await provider.GetIssueAsync("2", CancellationToken.None);

        withNullName.Labels.Should().Equal(string.Empty);
        withoutLabels.Labels.Should().BeEmpty();
    }

    [Fact]
    public async Task ListOpenIssuesAsync_HasMore_WhenExtraItemReturned()
    {
        // Request pageSize=2, return 3 items → HasMore=true, only 2 items returned
        StubGet(IssuesPath, new[]
        {
            BuildIssueJson(1, "First", "b", []),
            BuildIssueJson(2, "Second", "b", []),
            BuildIssueJson(3, "Third", "b", [])
        });

        await using var provider = CreateProvider();
        var result = await provider.ListOpenIssuesAsync(1, 2, CancellationToken.None);

        result.Items.Should().HaveCount(2);
        result.HasMore.Should().BeTrue();
    }

    [Fact]
    public async Task ListOpenIssuesAsync_PassesCorrectApiOptions()
    {
        StubGet(IssuesPath, Array.Empty<object>());

        await using var provider = CreateProvider();
        await provider.ListOpenIssuesAsync(3, 10, CancellationToken.None);

        var query = ListRequestQuery();
        query["page"].Should().Be("3");
        query["per_page"].Should().Be("11", "one extra item is fetched to detect HasMore");
        query["state"].Should().Be("open");
    }

    [Fact]
    public async Task ListOpenIssuesAsync_RateLimitException_WrapsAsCustomException()
    {
        // StubRateLimited answers 403 with a reset time one second in the past, so the resilience
        // pipeline's retry delay falls through to the default backoff instead of waiting minutes.
        var resetTime = DateTimeOffset.UtcNow.AddSeconds(-1);
        StubRateLimited(IssuesPath);

        await using var provider = CreateProvider();
        var act = () => provider.ListOpenIssuesAsync(1, 10, CancellationToken.None);

        var ex = await act.Should().ThrowAsync<CodingAgent.Pipeline.Models.RateLimitExceededException>();
        ex.Which.ResetAt.Should().BeCloseTo(resetTime, TimeSpan.FromSeconds(3));
        ex.Which.InnerException.Should().BeOfType<Octokit.RateLimitExceededException>();
    }

    [Fact]
    public async Task ListClosedIssuesAsync_MapsIssuesToSummaries()
    {
        StubGet(IssuesPath, new[]
        {
            BuildIssueJson(10, "Closed One", "b", ["agent:generated", "agent:done"]),
            BuildIssueJson(11, "Closed Two", "b", ["agent:generated", "agent:wont-do"])
        });

        await using var provider = CreateProvider();
        var result = await provider.ListClosedIssuesAsync(1, 25, null, null, CancellationToken.None);

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
        StubGet(IssuesPath, Array.Empty<object>());

        await using var provider = CreateProvider();
        var result = await provider.ListClosedIssuesAsync(1, 20, null, since, CancellationToken.None);

        result.Items.Should().BeEmpty();
        var query = ListRequestQuery();
        query["state"].Should().Be("closed");
        query["page"].Should().Be("1");
        query["per_page"].Should().Be("21");
        DateTimeOffset.Parse(query["since"], System.Globalization.CultureInfo.InvariantCulture)
            .UtcDateTime.Should().Be(since);
    }

    [Fact]
    public async Task ListClosedIssuesAsync_PassesLabelsToRequest()
    {
        StubGet(IssuesPath, Array.Empty<object>());

        await using var provider = CreateProvider();
        await provider.ListClosedIssuesAsync(1, 20, new List<string> { "agent:generated" }.AsReadOnly(), null, CancellationToken.None);

        ListRequestQuery()["labels"].Should().Be("agent:generated");
    }

    [Fact]
    public async Task ListClosedIssuesAsync_FiltersPullRequests()
    {
        StubGet(IssuesPath, new[]
        {
            BuildPullRequestIssueJson(1, "PR"),
            BuildIssueJson(2, "Real Issue", "b", ["agent:generated"])
        });

        await using var provider = CreateProvider();
        var result = await provider.ListClosedIssuesAsync(1, 25, null, null, CancellationToken.None);

        result.Items.Should().HaveCount(1);
        result.Items[0].Identifier.Should().Be("2");
    }

    [Fact]
    public async Task ListClosedIssuesAsync_HasMore_WhenExtraItemReturned()
    {
        StubGet(IssuesPath, new[]
        {
            BuildIssueJson(1, "A", "b", []),
            BuildIssueJson(2, "B", "b", []),
            BuildIssueJson(3, "C", "b", [])
        });

        await using var provider = CreateProvider();
        var result = await provider.ListClosedIssuesAsync(1, 2, null, null, CancellationToken.None);

        result.Items.Should().HaveCount(2);
        result.HasMore.Should().BeTrue();
    }

    [Fact]
    public async Task ListOpenIssuesAsync_HasMore_TrueWhenRawCountExceedsPageSizeDespitePrFiltering()
    {
        // 11 raw results: 8 real issues + 3 PRs, pageSize=10
        // Raw count 11 > 10 → HasMore=true even though filtered count (8) < pageSize
        var raw = new List<object>();
        for (var i = 1; i <= 8; i++)
            raw.Add(BuildIssueJson(i, $"Issue {i}", "b", []));
        for (var i = 9; i <= 11; i++)
            raw.Add(BuildPullRequestIssueJson(i, $"PR {i}"));
        StubGet(IssuesPath, raw);

        await using var provider = CreateProvider();
        var result = await provider.ListOpenIssuesAsync(1, 10, CancellationToken.None);

        result.HasMore.Should().BeTrue();
        result.Items.Should().HaveCountLessThanOrEqualTo(10);
    }

    [Fact]
    public async Task ListOpenIssuesAsync_HasMore_FalseWhenRawCountDoesNotExceedPageSize()
    {
        // 5 raw results, 0 PRs, pageSize=10
        // Raw count 5 ≤ 10 → HasMore=false
        var raw = new List<object>();
        for (var i = 1; i <= 5; i++)
            raw.Add(BuildIssueJson(i, $"Issue {i}", "b", []));
        StubGet(IssuesPath, raw);

        await using var provider = CreateProvider();
        var result = await provider.ListOpenIssuesAsync(1, 10, CancellationToken.None);

        result.HasMore.Should().BeFalse();
        result.Items.Should().HaveCount(5);
    }
}
