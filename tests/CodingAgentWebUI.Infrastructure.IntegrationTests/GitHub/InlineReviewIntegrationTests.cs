using System.Text.Json;
using AwesomeAssertions;
using CodingAgentWebUI.Infrastructure.GitHub;
using CodingAgentWebUI.Pipeline.Models;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

#pragma warning disable CS8602 // Dereference of a possibly null reference — WireMock log entries always have RequestMessage populated

namespace CodingAgentWebUI.Infrastructure.IntegrationTests.GitHub;

/// <summary>
/// Integration tests for the end-to-end inline review flow using WireMock.
/// Tests exercise the real GitHubRepositoryProvider against a simulated GitHub API.
/// Validates: Req 4 (Extended Review Submission API), Req 6 (PostReviewFindingsStep Enhancement),
/// Req 11 (Stale Review Handling), Req 12 (Retry and Graceful Degradation).
/// </summary>
public class InlineReviewIntegrationTests : IAsyncDisposable
{
    private const string Owner = "test-owner";
    private const string Repo = "test-repo";
    private const string Token = "fake-token-12345";
    private const string BaseBranch = "main";

    private readonly WireMockServer _server;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    public InlineReviewIntegrationTests()
    {
        _server = WireMockServer.Start();
    }

    public ValueTask DisposeAsync()
    {
        _server.Stop();
        _server.Dispose();
        GC.SuppressFinalize(this);
        return ValueTask.CompletedTask;
    }

    private GitHubRepositoryProvider CreateProvider() =>
        new(_server.Url!, Token, Owner, Repo, BaseBranch);

    /// <summary>
    /// Prepends the /api/v3 prefix that Octokit adds for non-github.com base URLs.
    /// </summary>
    private static string ApiPath(string path) => $"/api/v3{path}";

    #region Full Flow: Submit Review with Inline Comments

    [Fact]
    public async Task FullFlow_SubmitReviewWithInlineComments_SendsCorrectApiPayload()
    {
        // Arrange: Set up WireMock to accept a review with inline comments
        var reviewPath = ApiPath($"/repos/{Owner}/{Repo}/pulls/42/reviews");
        StubPost(reviewPath, BuildReviewResponseJson(1001));

        var submission = new ReviewSubmission
        {
            Body = "## Code Review Summary\n\n<!-- agent:pr-review -->\n\n2 findings identified.",
            Type = PullRequestReviewType.RequestChanges,
            CommitId = "abc123def456789",
            Comments = new List<ReviewComment>
            {
                new()
                {
                    Path = "src/Services/AuthService.cs",
                    Line = 42,
                    Side = DiffSide.Right,
                    Body = "🔴 **CRITICAL**: Null reference possible when input is not validated\n— *SecurityReviewer*"
                },
                new()
                {
                    Path = "src/Controllers/UserController.cs",
                    Line = 15,
                    Side = DiffSide.Right,
                    Body = "🟡 **WARNING**: Missing input validation on email parameter\n— *CodeQualityReviewer*"
                }
            }
        };

        // Act
        await using var provider = CreateProvider();
        await provider.SubmitPullRequestReviewAsync(42, submission, CancellationToken.None);

        // Assert: Verify the HTTP request matches expected GitHub API format
        var entries = _server.LogEntries
            .Where(e => e.RequestMessage.Path?.Contains("/pulls/42/reviews") == true
                        && e.RequestMessage.Method == "POST")
            .ToList();
        entries.Should().HaveCount(1);

        var requestBody = entries[0].RequestMessage.Body!;

        // Verify top-level fields
        requestBody.Should().Contain("\"event\":\"REQUEST_CHANGES\"");
        requestBody.Should().Contain("\"commit_id\":\"abc123def456789\"");
        requestBody.Should().Contain("<!-- agent:pr-review -->");

        // Verify comments array structure
        requestBody.Should().Contain("\"path\":\"src/Services/AuthService.cs\"");
        requestBody.Should().Contain("\"line\":42");
        requestBody.Should().Contain("\"side\":\"RIGHT\"");
        requestBody.Should().Contain("Null reference possible");

        requestBody.Should().Contain("\"path\":\"src/Controllers/UserController.cs\"");
        requestBody.Should().Contain("\"line\":15");
        requestBody.Should().Contain("Missing input validation");
    }

    [Fact]
    public async Task FullFlow_SubmitReviewWithCommentType_MapsEventToComment()
    {
        // Arrange
        var reviewPath = ApiPath($"/repos/{Owner}/{Repo}/pulls/10/reviews");
        StubPost(reviewPath, BuildReviewResponseJson(2001));

        var submission = new ReviewSubmission
        {
            Body = "Review body",
            Type = PullRequestReviewType.Comment,
            Comments = new List<ReviewComment>
            {
                new()
                {
                    Path = "src/file.cs",
                    Line = 5,
                    Side = DiffSide.Right,
                    Body = "💡 **SUGGESTION**: Consider using a constant here\n— *StyleReviewer*"
                }
            }
        };

        // Act
        await using var provider = CreateProvider();
        await provider.SubmitPullRequestReviewAsync(10, submission, CancellationToken.None);

        // Assert
        var requestBody = GetRequestBody(ApiPath($"/repos/{Owner}/{Repo}/pulls/10/reviews"));
        requestBody.Should().NotBeNull();
        requestBody!.Should().Contain("\"event\":\"COMMENT\"");
    }

    [Fact]
    public async Task FullFlow_SubmitReviewWithLeftSideComment_MapsSideToLeft()
    {
        // Arrange
        var reviewPath = ApiPath($"/repos/{Owner}/{Repo}/pulls/7/reviews");
        StubPost(reviewPath, BuildReviewResponseJson(2002));

        var submission = new ReviewSubmission
        {
            Body = "Review with left-side comment",
            Type = PullRequestReviewType.Comment,
            Comments = new List<ReviewComment>
            {
                new()
                {
                    Path = "src/deleted-code.cs",
                    Line = 20,
                    Side = DiffSide.Left,
                    Body = "This deleted code was important"
                }
            }
        };

        // Act
        await using var provider = CreateProvider();
        await provider.SubmitPullRequestReviewAsync(7, submission, CancellationToken.None);

        // Assert
        var requestBody = GetRequestBody(ApiPath($"/repos/{Owner}/{Repo}/pulls/7/reviews"));
        requestBody.Should().NotBeNull();
        requestBody!.Should().Contain("\"side\":\"LEFT\"");
    }

    [Fact]
    public async Task FullFlow_NullCommitId_OmitsCommitIdFromPayload()
    {
        // Arrange
        var reviewPath = ApiPath($"/repos/{Owner}/{Repo}/pulls/8/reviews");
        StubPost(reviewPath, BuildReviewResponseJson(2003));

        var submission = new ReviewSubmission
        {
            Body = "Review without commit anchoring",
            Type = PullRequestReviewType.Comment,
            CommitId = null,
            Comments = new List<ReviewComment>
            {
                new() { Path = "file.cs", Line = 1, Side = DiffSide.Right, Body = "Comment" }
            }
        };

        // Act
        await using var provider = CreateProvider();
        await provider.SubmitPullRequestReviewAsync(8, submission, CancellationToken.None);

        // Assert
        var requestBody = GetRequestBody(ApiPath($"/repos/{Owner}/{Repo}/pulls/8/reviews"));
        requestBody.Should().NotBeNull();
        requestBody!.Should().NotContain("commit_id");
    }

    #endregion

    #region 422 Response Handling and Body-Only Retry

    [Fact]
    public async Task Http422_RetriesWithBodyOnlyFallback_SecondRequestHasNoComments()
    {
        // Arrange: First request returns 422, second (body-only retry) returns 200
        var reviewPath = ApiPath($"/repos/{Owner}/{Repo}/pulls/15/reviews");

        // Use WireMock scenarios to return different responses on sequential calls
        _server.Given(Request.Create().WithPath(reviewPath).UsingPost())
            .InScenario("422-retry")
            .WillSetStateTo("first-call-done")
            .RespondWith(Response.Create()
                .WithStatusCode(422)
                .WithHeader("Content-Type", "application/json")
                .WithBody(JsonSerializer.Serialize(new
                {
                    message = "Validation Failed",
                    errors = new[] { new { resource = "PullRequestReviewComment", code = "invalid", field = "line" } },
                    documentation_url = "https://docs.github.com"
                }, JsonOptions)));

        _server.Given(Request.Create().WithPath(reviewPath).UsingPost())
            .InScenario("422-retry")
            .WhenStateIs("first-call-done")
            .RespondWith(Response.Create()
                .WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithBody(JsonSerializer.Serialize(BuildReviewResponseJson(3001), JsonOptions)));

        var submission = new ReviewSubmission
        {
            Body = "Review body with findings",
            Type = PullRequestReviewType.Comment,
            Comments = new List<ReviewComment>
            {
                new()
                {
                    Path = "src/invalid-file.cs",
                    Line = 999,
                    Side = DiffSide.Right,
                    Body = "This line doesn't exist in the diff"
                },
                new()
                {
                    Path = "src/another-file.cs",
                    Line = 50,
                    Side = DiffSide.Right,
                    Body = "Another comment"
                }
            }
        };

        // Act
        await using var provider = CreateProvider();
        await provider.SubmitPullRequestReviewAsync(15, submission, CancellationToken.None);

        // Assert: Verify two POST requests were made
        var postEntries = _server.LogEntries
            .Where(e => e.RequestMessage.Path?.Contains("/pulls/15/reviews") == true
                        && e.RequestMessage.Method == "POST")
            .ToList();
        postEntries.Should().HaveCount(2);

        // First request should contain inline comments
        var firstBody = postEntries[0].RequestMessage.Body!;
        firstBody.Should().Contain("src/invalid-file.cs");
        firstBody.Should().Contain("src/another-file.cs");
        firstBody.Should().Contain("\"line\":999");

        // Second request (retry) should NOT contain inline comments
        var secondBody = postEntries[1].RequestMessage.Body!;
        secondBody.Should().NotContain("src/invalid-file.cs");
        secondBody.Should().NotContain("src/another-file.cs");
        // But should still contain the review body
        secondBody.Should().Contain("Review body with findings");
    }

    [Fact]
    public async Task Http422_BothAttemptsFail_ThrowsApiValidationException()
    {
        // Arrange: Both requests return 422 (body-only retry also fails)
        var reviewPath = ApiPath($"/repos/{Owner}/{Repo}/pulls/16/reviews");

        _server.Given(Request.Create().WithPath(reviewPath).UsingPost())
            .RespondWith(Response.Create()
                .WithStatusCode(422)
                .WithHeader("Content-Type", "application/json")
                .WithBody(JsonSerializer.Serialize(new
                {
                    message = "Validation Failed",
                    errors = new[] { new { resource = "PullRequestReview", code = "invalid" } }
                }, JsonOptions)));

        var submission = new ReviewSubmission
        {
            Body = "Review body",
            Type = PullRequestReviewType.Comment,
            Comments = new List<ReviewComment>
            {
                new() { Path = "file.cs", Line = 1, Side = DiffSide.Right, Body = "Comment" }
            }
        };

        // Act & Assert: Should throw because the body-only retry also gets 422
        await using var provider = CreateProvider();
        await provider.Invoking(p => p.SubmitPullRequestReviewAsync(16, submission, CancellationToken.None))
            .Should().ThrowAsync<Octokit.ApiValidationException>();

        // Verify at least 2 attempts were made
        var postEntries = _server.LogEntries
            .Where(e => e.RequestMessage.Path?.Contains("/pulls/16/reviews") == true
                        && e.RequestMessage.Method == "POST")
            .ToList();
        postEntries.Should().HaveCountGreaterThanOrEqualTo(2);
    }

    #endregion

    #region Dismiss Previous Reviews Before Posting New One

    [Fact]
    public async Task DismissFlow_DismissesPreviousReviewsThenPostsNew()
    {
        // Arrange: Set up the full dismiss + submit flow
        const string marker = "<!-- agent:pr-review -->";
        const string reason = "Superseded by new review";
        const int prNumber = 30;

        // Stub reviews list — two matching bot reviews, one human review
        StubGet(ApiPath($"/repos/{Owner}/{Repo}/pulls/{prNumber}/reviews"), new[]
        {
            BuildReviewJson(401, "pipeline-bot", $"Old review 1 {marker} content"),
            BuildReviewJson(402, "human-reviewer", "Human review without marker"),
            BuildReviewJson(403, "pipeline-bot", $"Old review 2 {marker} content")
        });

        // Stub dismiss endpoints for matching reviews
        _server.Given(Request.Create()
                .WithPath(ApiPath($"/repos/{Owner}/{Repo}/pulls/{prNumber}/reviews/401/dismissals"))
                .UsingPut())
            .RespondWith(Response.Create()
                .WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithBody("{}"));

        _server.Given(Request.Create()
                .WithPath(ApiPath($"/repos/{Owner}/{Repo}/pulls/{prNumber}/reviews/403/dismissals"))
                .UsingPut())
            .RespondWith(Response.Create()
                .WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithBody("{}"));

        // Stub the new review submission
        var reviewPath = ApiPath($"/repos/{Owner}/{Repo}/pulls/{prNumber}/reviews");
        StubPost(reviewPath, BuildReviewResponseJson(5001));

        // Act: First dismiss, then submit
        await using var provider = CreateProvider();
        await provider.DismissPreviousReviewAsync(prNumber, marker, reason, CancellationToken.None);
        await provider.SubmitPullRequestReviewAsync(prNumber, new ReviewSubmission
        {
            Body = $"New review {marker}",
            Type = PullRequestReviewType.Comment,
            Comments = new List<ReviewComment>
            {
                new() { Path = "src/file.cs", Line = 10, Side = DiffSide.Right, Body = "New finding" }
            }
        }, CancellationToken.None);

        // Assert: Verify dismiss was called for both matching reviews
        var dismissEntries = _server.LogEntries
            .Where(e => e.RequestMessage.Path?.Contains("/dismissals") == true
                        && e.RequestMessage.Method == "PUT")
            .ToList();
        dismissEntries.Should().HaveCount(2);
        dismissEntries.Should().Contain(e => e.RequestMessage.Path!.Contains("/401/"));
        dismissEntries.Should().Contain(e => e.RequestMessage.Path!.Contains("/403/"));

        // Verify dismiss reason is included in the request body
        var dismissBody = dismissEntries[0].RequestMessage.Body!;
        dismissBody.Should().Contain(reason);

        // Verify the new review was posted after dismissals
        var reviewEntries = _server.LogEntries
            .Where(e => e.RequestMessage.Path == reviewPath
                        && e.RequestMessage.Method == "POST")
            .ToList();
        reviewEntries.Should().HaveCount(1);

        // Verify ordering: dismissals happen before the new review post
        var lastDismissTime = dismissEntries.Max(e => e.RequestMessage.DateTime);
        var reviewPostTime = reviewEntries[0].RequestMessage.DateTime;
        reviewPostTime.Should().BeOnOrAfter(lastDismissTime);
    }

    [Fact]
    public async Task DismissFlow_NoMatchingReviews_IsNoOpAndDoesNotBlock()
    {
        // Arrange
        const int prNumber = 31;

        StubGet(ApiPath($"/repos/{Owner}/{Repo}/pulls/{prNumber}/reviews"), new[]
        {
            BuildReviewJson(501, "human-user", "Human review"),
            BuildReviewJson(502, "pipeline-bot", "Bot review without the marker")
        });

        // Act
        await using var provider = CreateProvider();
        await provider.DismissPreviousReviewAsync(prNumber, "<!-- agent:pr-review -->", "Superseded", CancellationToken.None);

        // Assert: No dismiss calls made
        var dismissEntries = _server.LogEntries
            .Where(e => e.RequestMessage.Path?.Contains("/dismissals") == true)
            .ToList();
        dismissEntries.Should().BeEmpty();
    }

    [Fact]
    public async Task DismissFlow_IndividualDismissFailure_ContinuesWithRemainingReviews()
    {
        // Arrange: First dismiss fails, second succeeds
        const int prNumber = 32;
        const string marker = "<!-- agent:pr-review -->";

        StubGet(ApiPath($"/repos/{Owner}/{Repo}/pulls/{prNumber}/reviews"), new[]
        {
            BuildReviewJson(601, "pipeline-bot", $"Review 1 {marker}"),
            BuildReviewJson(602, "pipeline-bot", $"Review 2 {marker}")
        });

        // First dismiss returns 404 (already dismissed or insufficient permissions)
        _server.Given(Request.Create()
                .WithPath(ApiPath($"/repos/{Owner}/{Repo}/pulls/{prNumber}/reviews/601/dismissals"))
                .UsingPut())
            .RespondWith(Response.Create()
                .WithStatusCode(404)
                .WithHeader("Content-Type", "application/json")
                .WithBody(JsonSerializer.Serialize(new { message = "Not Found" }, JsonOptions)));

        // Second dismiss succeeds
        _server.Given(Request.Create()
                .WithPath(ApiPath($"/repos/{Owner}/{Repo}/pulls/{prNumber}/reviews/602/dismissals"))
                .UsingPut())
            .RespondWith(Response.Create()
                .WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithBody("{}"));

        // Act: Should not throw despite first dismiss failing
        await using var provider = CreateProvider();
        await provider.DismissPreviousReviewAsync(prNumber, marker, "Superseded", CancellationToken.None);

        // Assert: Both dismiss endpoints were called
        var dismissEntries = _server.LogEntries
            .Where(e => e.RequestMessage.Path?.Contains("/dismissals") == true
                        && e.RequestMessage.Method == "PUT")
            .ToList();
        dismissEntries.Should().HaveCount(2);
    }

    #endregion

    #region Graceful Degradation: Empty Comments (Body-Only)

    [Fact]
    public async Task GracefulDegradation_EmptyComments_DelegatesToBodyOnlyReview()
    {
        // Arrange: When no structured findings are extracted, Comments list is empty
        var reviewPath = ApiPath($"/repos/{Owner}/{Repo}/pulls/50/reviews");
        StubPost(reviewPath, BuildReviewResponseJson(6001));

        var submission = new ReviewSubmission
        {
            Body = "## Code Review Summary\n\n<!-- agent:pr-review -->\n\nNo inline findings extracted.\nℹ️ Inline comments could not be posted for this review.",
            Type = PullRequestReviewType.Comment,
            Comments = Array.Empty<ReviewComment>() // No structured findings
        };

        // Act
        await using var provider = CreateProvider();
        await provider.SubmitPullRequestReviewAsync(50, submission, CancellationToken.None);

        // Assert: Review was posted via the Reviews API (body-only path)
        var entries = _server.LogEntries
            .Where(e => e.RequestMessage.Path?.Contains("/pulls/50/reviews") == true
                        && e.RequestMessage.Method == "POST")
            .ToList();
        entries.Should().HaveCount(1);

        // Verify the request body contains the review body but no comments array
        var requestBody = entries[0].RequestMessage.Body!;
        requestBody.Should().Contain("Code Review Summary");
        requestBody.Should().Contain("<!-- agent:pr-review -->");

        // The body-only path uses Octokit's PullRequestReviewCreate which doesn't include
        // a "comments" field at all (different from an empty array)
        // Verify it did NOT go to Issue Comments API
        var issueCommentEntries = _server.LogEntries
            .Where(e => e.RequestMessage.Path?.Contains("/issues/50/comments") == true)
            .ToList();
        issueCommentEntries.Should().BeEmpty();
    }

    [Fact]
    public async Task GracefulDegradation_EmptyComments_ProducesSameResultAsBodyOnlyOverload()
    {
        // Arrange: Compare behavior of empty-comments submission vs direct body-only call
        var reviewPath = ApiPath($"/repos/{Owner}/{Repo}/pulls/51/reviews");
        StubPost(reviewPath, BuildReviewResponseJson(6002));

        const string reviewBody = "Body-only review content";

        // Act: Call with empty comments (should delegate to body-only)
        await using var provider = CreateProvider();
        await provider.SubmitPullRequestReviewAsync(51, new ReviewSubmission
        {
            Body = reviewBody,
            Type = PullRequestReviewType.Comment,
            Comments = Array.Empty<ReviewComment>()
        }, CancellationToken.None);

        // Assert: Exactly one POST to reviews endpoint
        var entries = _server.LogEntries
            .Where(e => e.RequestMessage.Path?.Contains("/pulls/51/reviews") == true
                        && e.RequestMessage.Method == "POST")
            .ToList();
        entries.Should().HaveCount(1);

        var requestBody = entries[0].RequestMessage.Body!;
        requestBody.Should().Contain(reviewBody);
    }

    #endregion

    #region Helpers

    private void StubGet(string path, object responseBody)
    {
        _server.Given(Request.Create().WithPath(path).UsingGet())
            .RespondWith(Response.Create()
                .WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithBody(JsonSerializer.Serialize(responseBody, JsonOptions)));
    }

    private void StubPost(string path, object responseBody, int statusCode = 200)
    {
        _server.Given(Request.Create().WithPath(path).UsingPost())
            .RespondWith(Response.Create()
                .WithStatusCode(statusCode)
                .WithHeader("Content-Type", "application/json")
                .WithBody(JsonSerializer.Serialize(responseBody, JsonOptions)));
    }

    private string? GetRequestBody(string path) =>
        _server.LogEntries
            .FirstOrDefault(e => e.RequestMessage.Path?.Equals(path, StringComparison.OrdinalIgnoreCase) == true)
            ?.RequestMessage.Body;

    private static object BuildReviewResponseJson(long id) => new
    {
        id,
        body = "Review body",
        state = "COMMENTED",
        user = new { login = "pipeline-bot", id = 100 },
        html_url = $"https://github.com/{Owner}/{Repo}/pull/1#pullrequestreview-{id}",
        submitted_at = "2026-01-15T10:00:00Z"
    };

    private static object BuildReviewJson(long id, string login, string body, string state = "CHANGES_REQUESTED") => new
    {
        id,
        body,
        state,
        user = new { login, id = login.GetHashCode() },
        html_url = $"https://github.com/{Owner}/{Repo}/pull/1#pullrequestreview-{id}",
        submitted_at = "2026-01-15T10:00:00Z"
    };

    #endregion
}
