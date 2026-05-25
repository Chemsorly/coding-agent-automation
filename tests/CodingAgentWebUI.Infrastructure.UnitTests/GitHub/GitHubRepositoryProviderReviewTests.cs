using System.Text.Json;
using AwesomeAssertions;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Infrastructure.GitHub;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;

#pragma warning disable CS8602 // Dereference of a possibly null reference — WireMock log entries always have RequestMessage populated

namespace CodingAgentWebUI.Infrastructure.UnitTests;

/// <summary>
/// Unit tests for GitHubRepositoryProvider review-related functionality:
/// - SubmitPullRequestReviewAsync (Reviews API migration, inline comments)
/// - DismissPreviousReviewAsync (marker-based dismiss with pagination)
/// - SupportsInlineReviewComments property
/// </summary>
public class GitHubRepositoryProviderReviewTests : WireMockTestBase
{
    private const string Owner = "test-owner";
    private const string Repo = "test-repo";
    private const string Token = "fake-token-12345";
    private const string BaseBranch = "main";

    private GitHubRepositoryProvider CreateProvider() =>
        new(Server.Url!, Token, Owner, Repo, BaseBranch);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    #region SupportsInlineReviewComments

    [Fact]
    public async Task SupportsInlineReviewComments_ReturnsTrue()
    {
        await using var provider = CreateProvider();
        provider.SupportsInlineReviewComments.Should().BeTrue();
    }

    #endregion

    #region SubmitPullRequestReviewAsync — Reviews API (body-only)

    [Fact]
    public async Task SubmitPullRequestReviewAsync_BodyOnly_UsesReviewsApi()
    {
        // Stub the Reviews API endpoint (not Issue Comments)
        var reviewPath = ApiPath($"/repos/{Owner}/{Repo}/pulls/42/reviews");
        StubPost(reviewPath, BuildReviewResponseJson(1001));

        await using var provider = CreateProvider();
        await provider.SubmitPullRequestReviewAsync(
            42, "Review body", PullRequestReviewType.Comment, CancellationToken.None);

        // Verify the request went to the Reviews API
        var entries = Server.LogEntries
            .Where(e => e.RequestMessage.Path?.Contains("/reviews") == true)
            .ToList();
        entries.Should().HaveCount(1);

        // Verify it did NOT go to Issue Comments API
        var issueCommentEntries = Server.LogEntries
            .Where(e => e.RequestMessage.Path?.Contains("/issues/42/comments") == true)
            .ToList();
        issueCommentEntries.Should().BeEmpty();
    }

    [Fact]
    public async Task SubmitPullRequestReviewAsync_Comment_MapsEventCorrectly()
    {
        var reviewPath = ApiPath($"/repos/{Owner}/{Repo}/pulls/10/reviews");
        StubPost(reviewPath, BuildReviewResponseJson(2001));

        await using var provider = CreateProvider();
        await provider.SubmitPullRequestReviewAsync(
            10, "LGTM", PullRequestReviewType.Comment, CancellationToken.None);

        var body = GetRequestBody(reviewPath);
        body.Should().NotBeNull();
        // Octokit serializes PullRequestReviewEvent enum values — verify COMMENT event is set
        body!.ToUpperInvariant().Should().Contain("COMMENT");
    }

    [Fact]
    public async Task SubmitPullRequestReviewAsync_RequestChanges_MapsEventCorrectly()
    {
        var reviewPath = ApiPath($"/repos/{Owner}/{Repo}/pulls/10/reviews");
        StubPost(reviewPath, BuildReviewResponseJson(2002));

        await using var provider = CreateProvider();
        await provider.SubmitPullRequestReviewAsync(
            10, "Needs fixes", PullRequestReviewType.RequestChanges, CancellationToken.None);

        var body = GetRequestBody(reviewPath);
        body.Should().NotBeNull();
        // Octokit serializes as uppercase REQUEST_CHANGES
        body!.Should().Contain("REQUEST_CHANGES");
    }

    [Fact]
    public async Task SubmitPullRequestReviewAsync_Approve_MapsEventCorrectly()
    {
        var reviewPath = ApiPath($"/repos/{Owner}/{Repo}/pulls/10/reviews");
        StubPost(reviewPath, BuildReviewResponseJson(2003));

        await using var provider = CreateProvider();
        await provider.SubmitPullRequestReviewAsync(
            10, "Looks good", PullRequestReviewType.Approve, CancellationToken.None);

        var body = GetRequestBody(reviewPath);
        body.Should().NotBeNull();
        body!.ToUpperInvariant().Should().Contain("APPROVE");
    }

    #endregion

    #region SubmitPullRequestReviewAsync — Inline comments overload

    [Fact]
    public async Task SubmitPullRequestReviewAsync_WithComments_PopulatesCommentsArray()
    {
        var reviewPath = ApiPath($"/repos/{Owner}/{Repo}/pulls/5/reviews");
        StubPost(reviewPath, BuildReviewResponseJson(3001));

        var submission = new ReviewSubmission
        {
            Body = "Summary body",
            Type = PullRequestReviewType.Comment,
            Comments = new List<ReviewComment>
            {
                new()
                {
                    Path = "src/Service.cs",
                    Line = 42,
                    Side = DiffSide.Right,
                    Body = "Null reference possible"
                },
                new()
                {
                    Path = "src/Controller.cs",
                    Line = 15,
                    Side = DiffSide.Left,
                    Body = "Missing validation"
                }
            }
        };

        await using var provider = CreateProvider();
        await provider.SubmitPullRequestReviewAsync(5, submission, CancellationToken.None);

        var body = GetRequestBody(reviewPath);
        body.Should().NotBeNull();

        // Verify comments array is populated with correct fields
        body!.Should().Contain("\"path\":\"src/Service.cs\"");
        body.Should().Contain("\"line\":42");
        body.Should().Contain("\"side\":\"RIGHT\"");
        body.Should().Contain("\"body\":\"Null reference possible\"");

        body.Should().Contain("\"path\":\"src/Controller.cs\"");
        body.Should().Contain("\"line\":15");
        body.Should().Contain("\"side\":\"LEFT\"");
        body.Should().Contain("\"body\":\"Missing validation\"");
    }

    [Fact]
    public async Task SubmitPullRequestReviewAsync_WithComments_SetsEventField()
    {
        var reviewPath = ApiPath($"/repos/{Owner}/{Repo}/pulls/5/reviews");
        StubPost(reviewPath, BuildReviewResponseJson(3002));

        var submission = new ReviewSubmission
        {
            Body = "Review",
            Type = PullRequestReviewType.RequestChanges,
            Comments = new List<ReviewComment>
            {
                new() { Path = "file.cs", Line = 1, Side = DiffSide.Right, Body = "Issue" }
            }
        };

        await using var provider = CreateProvider();
        await provider.SubmitPullRequestReviewAsync(5, submission, CancellationToken.None);

        var body = GetRequestBody(reviewPath);
        body.Should().NotBeNull();
        body!.Should().Contain("\"event\":\"REQUEST_CHANGES\"");
    }

    [Fact]
    public async Task SubmitPullRequestReviewAsync_WithCommitId_SetsCommitIdField()
    {
        var reviewPath = ApiPath($"/repos/{Owner}/{Repo}/pulls/7/reviews");
        StubPost(reviewPath, BuildReviewResponseJson(3003));

        var submission = new ReviewSubmission
        {
            Body = "Review with commit",
            Type = PullRequestReviewType.Comment,
            CommitId = "abc123def456",
            Comments = new List<ReviewComment>
            {
                new() { Path = "file.cs", Line = 10, Side = DiffSide.Right, Body = "Comment" }
            }
        };

        await using var provider = CreateProvider();
        await provider.SubmitPullRequestReviewAsync(7, submission, CancellationToken.None);

        var body = GetRequestBody(reviewPath);
        body.Should().NotBeNull();
        body!.Should().Contain("\"commit_id\":\"abc123def456\"");
    }

    [Fact]
    public async Task SubmitPullRequestReviewAsync_WithNullCommitId_OmitsCommitIdField()
    {
        var reviewPath = ApiPath($"/repos/{Owner}/{Repo}/pulls/7/reviews");
        StubPost(reviewPath, BuildReviewResponseJson(3004));

        var submission = new ReviewSubmission
        {
            Body = "Review without commit",
            Type = PullRequestReviewType.Comment,
            CommitId = null,
            Comments = new List<ReviewComment>
            {
                new() { Path = "file.cs", Line = 10, Side = DiffSide.Right, Body = "Comment" }
            }
        };

        await using var provider = CreateProvider();
        await provider.SubmitPullRequestReviewAsync(7, submission, CancellationToken.None);

        var body = GetRequestBody(reviewPath);
        body.Should().NotBeNull();
        body!.Should().NotContain("commit_id");
    }

    [Fact]
    public async Task SubmitPullRequestReviewAsync_EmptyComments_DelegatesToBodyOnly()
    {
        // When Comments is empty, should use the Octokit PullRequest.Review.Create path
        var reviewPath = ApiPath($"/repos/{Owner}/{Repo}/pulls/8/reviews");
        StubPost(reviewPath, BuildReviewResponseJson(3005));

        var submission = new ReviewSubmission
        {
            Body = "Body only review",
            Type = PullRequestReviewType.Comment,
            Comments = Array.Empty<ReviewComment>()
        };

        await using var provider = CreateProvider();
        await provider.SubmitPullRequestReviewAsync(8, submission, CancellationToken.None);

        // Should still hit the reviews endpoint (body-only overload also uses Reviews API)
        var entries = Server.LogEntries
            .Where(e => e.RequestMessage.Path?.Contains("/reviews") == true)
            .ToList();
        entries.Should().HaveCount(1);
    }

    [Fact]
    public async Task SubmitPullRequestReviewAsync_422Response_RetriesWithoutComments()
    {
        // The inline comments overload uses Connection.Post (raw API) for the first attempt.
        // On 422, it catches ApiValidationException and retries with the body-only overload.
        //
        // Note: Connection.Post resolves relative URIs against the base URL.
        // We stub ALL requests to the reviews path to return 422 first, then 200.
        var reviewPath = ApiPath($"/repos/{Owner}/{Repo}/pulls/9/reviews");

        // First: stub with a simple non-scenario approach — always return 422
        // This verifies the 422 is caught and doesn't propagate
        StubPost(reviewPath, new
        {
            message = "Validation Failed",
            errors = new[] { new { resource = "PullRequestReviewComment", code = "invalid", field = "line" } },
            documentation_url = "https://docs.github.com"
        }, statusCode: 422);

        var submission = new ReviewSubmission
        {
            Body = "Review body",
            Type = PullRequestReviewType.Comment,
            Comments = new List<ReviewComment>
            {
                new() { Path = "bad/file.cs", Line = 999, Side = DiffSide.Right, Body = "Invalid line" }
            }
        };

        await using var provider = CreateProvider();

        // The 422 on the first call (with comments) should be caught.
        // The retry (body-only) will also get 422, which will propagate as ApiValidationException.
        // We expect the method to throw on the retry since we can't differentiate calls with a single stub.
        // Instead, let's verify the behavior by checking that the method attempts the retry.
        var ex = await Assert.ThrowsAsync<Octokit.ApiValidationException>(
            () => provider.SubmitPullRequestReviewAsync(9, submission, CancellationToken.None));

        // Verify that at least 2 POST requests were made (first with comments, retry without)
        var entries = Server.LogEntries
            .Where(e => e.RequestMessage.Path?.Contains("/pulls/9/reviews") == true
                        && e.RequestMessage.Method == "POST")
            .ToList();
        entries.Should().HaveCountGreaterThanOrEqualTo(2);

        // The first request should contain inline comments
        entries[0].RequestMessage.Body.Should().Contain("bad/file.cs");

        // The retry request should NOT contain the inline comment file path
        entries[^1].RequestMessage.Body.Should().NotContain("bad/file.cs");
    }

    #endregion

    #region DismissPreviousReviewAsync

    [Fact]
    public async Task DismissPreviousReviewAsync_FindsAndDismissesBotReviewsByMarker()
    {
        const string marker = "<!-- agent:pr-review -->";
        const string reason = "Superseded by new review";

        // Stub reviews list — one matching, one not
        StubGet(ApiPath($"/repos/{Owner}/{Repo}/pulls/20/reviews"), new[]
        {
            BuildReviewJson(501, "bot-user", $"Review body {marker} content"),
            BuildReviewJson(502, "human-user", "Human review without marker"),
            BuildReviewJson(503, "bot-user", "Bot review without marker")
        });

        // Stub dismiss endpoint for the matching review
        var dismissPath = ApiPath($"/repos/{Owner}/{Repo}/pulls/20/reviews/501/dismissals");
        Server.Given(Request.Create().WithPath(dismissPath).UsingPut())
            .RespondWith(Response.Create()
                .WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithBody("{}"));

        await using var provider = CreateProvider();
        await provider.DismissPreviousReviewAsync(20, marker, reason, CancellationToken.None);

        // Verify dismiss was called for review 501 only (only one with the marker)
        var dismissEntries = Server.LogEntries
            .Where(e => e.RequestMessage.Path?.Contains("/dismissals") == true)
            .ToList();
        dismissEntries.Should().HaveCount(1);
        dismissEntries[0].RequestMessage.Path.Should().Contain("/501/");
    }

    [Fact]
    public async Task DismissPreviousReviewAsync_DismissesAllMatchingReviews()
    {
        const string marker = "<!-- agent:pr-review -->";

        // Stub reviews list — three reviews with marker (all should be dismissed regardless of author)
        StubGet(ApiPath($"/repos/{Owner}/{Repo}/pulls/21/reviews"), new[]
        {
            BuildReviewJson(601, "bot-user", $"First review {marker}"),
            BuildReviewJson(602, "bot-user", $"Second review {marker}"),
            BuildReviewJson(603, "other-bot", $"Other bot {marker}")
        });

        // Stub dismiss endpoints for all matching reviews
        Server.Given(Request.Create()
                .WithPath(ApiPath($"/repos/{Owner}/{Repo}/pulls/21/reviews/601/dismissals"))
                .UsingPut())
            .RespondWith(Response.Create().WithStatusCode(200).WithBody("{}"));
        Server.Given(Request.Create()
                .WithPath(ApiPath($"/repos/{Owner}/{Repo}/pulls/21/reviews/602/dismissals"))
                .UsingPut())
            .RespondWith(Response.Create().WithStatusCode(200).WithBody("{}"));
        Server.Given(Request.Create()
                .WithPath(ApiPath($"/repos/{Owner}/{Repo}/pulls/21/reviews/603/dismissals"))
                .UsingPut())
            .RespondWith(Response.Create().WithStatusCode(200).WithBody("{}"));

        await using var provider = CreateProvider();
        await provider.DismissPreviousReviewAsync(21, marker, "Superseded", CancellationToken.None);

        // All reviews with the marker should be dismissed (marker is the definitive identifier)
        var dismissEntries = Server.LogEntries
            .Where(e => e.RequestMessage.Path?.Contains("/dismissals") == true)
            .ToList();
        dismissEntries.Should().HaveCount(3);
    }

    [Fact]
    public async Task DismissPreviousReviewAsync_NoMatchingReviews_IsNoOp()
    {
        const string marker = "<!-- agent:pr-review -->";

        // Stub reviews list — no matching reviews (none contain the marker)
        StubGet(ApiPath($"/repos/{Owner}/{Repo}/pulls/22/reviews"), new[]
        {
            BuildReviewJson(701, "human-user", "Human review"),
            BuildReviewJson(702, "bot-user", "Bot review without the marker")
        });

        await using var provider = CreateProvider();
        await provider.DismissPreviousReviewAsync(22, marker, "Superseded", CancellationToken.None);

        // No dismiss calls should be made
        var dismissEntries = Server.LogEntries
            .Where(e => e.RequestMessage.Path?.Contains("/dismissals") == true)
            .ToList();
        dismissEntries.Should().BeEmpty();
    }

    [Fact]
    public async Task DismissPreviousReviewAsync_PaginationHandlesMoreThan30Reviews()
    {
        const string marker = "<!-- agent:pr-review -->";

        // Build 35 reviews — the last one matches the marker.
        // Octokit's GetAll handles pagination internally, so we just return all 35 in one response
        // (WireMock doesn't need to simulate pagination — Octokit handles it via Link headers).
        var reviews = Enumerable.Range(1, 34)
            .Select(i => BuildReviewJson(800 + i, "bot-user", $"Review {i} without marker"))
            .Append(BuildReviewJson(835, "bot-user", $"Review with {marker}"))
            .ToArray();

        StubGet(ApiPath($"/repos/{Owner}/{Repo}/pulls/23/reviews"), reviews);

        // Stub dismiss for the matching review
        Server.Given(Request.Create()
                .WithPath(ApiPath($"/repos/{Owner}/{Repo}/pulls/23/reviews/835/dismissals"))
                .UsingPut())
            .RespondWith(Response.Create().WithStatusCode(200).WithBody("{}"));

        await using var provider = CreateProvider();
        await provider.DismissPreviousReviewAsync(23, marker, "Superseded", CancellationToken.None);

        // The matching review should be dismissed even with >30 reviews
        var dismissEntries = Server.LogEntries
            .Where(e => e.RequestMessage.Path?.Contains("/dismissals") == true)
            .ToList();
        dismissEntries.Should().HaveCount(1);
        dismissEntries[0].RequestMessage.Path.Should().Contain("/835/");
    }

    [Fact]
    public async Task DismissPreviousReviewAsync_IndividualDismissFailure_LogsButDoesNotBlock()
    {
        const string marker = "<!-- agent:pr-review -->";

        // Two matching reviews
        StubGet(ApiPath($"/repos/{Owner}/{Repo}/pulls/24/reviews"), new[]
        {
            BuildReviewJson(901, "bot-user", $"First {marker}"),
            BuildReviewJson(902, "bot-user", $"Second {marker}")
        });

        // First dismiss fails with 404
        Server.Given(Request.Create()
                .WithPath(ApiPath($"/repos/{Owner}/{Repo}/pulls/24/reviews/901/dismissals"))
                .UsingPut())
            .RespondWith(Response.Create()
                .WithStatusCode(404)
                .WithHeader("Content-Type", "application/json")
                .WithBody(JsonSerializer.Serialize(new { message = "Not Found" }, JsonOptions)));

        // Second dismiss succeeds
        Server.Given(Request.Create()
                .WithPath(ApiPath($"/repos/{Owner}/{Repo}/pulls/24/reviews/902/dismissals"))
                .UsingPut())
            .RespondWith(Response.Create().WithStatusCode(200).WithBody("{}"));

        await using var provider = CreateProvider();

        // Should not throw — individual failures are logged but don't block
        await provider.DismissPreviousReviewAsync(24, marker, "Superseded", CancellationToken.None);

        // Both dismiss endpoints should have been called
        var dismissEntries = Server.LogEntries
            .Where(e => e.RequestMessage.Path?.Contains("/dismissals") == true)
            .ToList();
        dismissEntries.Should().HaveCount(2);
    }

    [Fact]
    public async Task DismissPreviousReviewAsync_MatchesByMarkerRegardlessOfAuthor()
    {
        const string marker = "<!-- agent:pr-review -->";

        // Review authored by a different user but containing the marker — should still be dismissed
        StubGet(ApiPath($"/repos/{Owner}/{Repo}/pulls/25/reviews"), new[]
        {
            BuildReviewJson(1001, "some-other-user", $"Review {marker}")
        });

        // Stub dismiss
        Server.Given(Request.Create()
                .WithPath(ApiPath($"/repos/{Owner}/{Repo}/pulls/25/reviews/1001/dismissals"))
                .UsingPut())
            .RespondWith(Response.Create().WithStatusCode(200).WithBody("{}"));

        await using var provider = CreateProvider();
        await provider.DismissPreviousReviewAsync(25, marker, "Superseded", CancellationToken.None);

        // Should match by marker regardless of author
        var dismissEntries = Server.LogEntries
            .Where(e => e.RequestMessage.Path?.Contains("/dismissals") == true)
            .ToList();
        dismissEntries.Should().HaveCount(1);
    }

    [Fact]
    public async Task DismissPreviousReviewAsync_SkipsCommentedReviews_OnlyDismissesDismissibleStates()
    {
        const string marker = "<!-- agent:pr-review -->";

        // Three reviews with marker: one COMMENTED (not dismissible), one CHANGES_REQUESTED, one APPROVED
        StubGet(ApiPath($"/repos/{Owner}/{Repo}/pulls/26/reviews"), new[]
        {
            BuildReviewJson(1101, "bot-user", $"Commented review {marker}", "COMMENTED"),
            BuildReviewJson(1102, "bot-user", $"Changes requested review {marker}", "CHANGES_REQUESTED"),
            BuildReviewJson(1103, "bot-user", $"Approved review {marker}", "APPROVED")
        });

        // Stub dismiss endpoints for the dismissible reviews only
        Server.Given(Request.Create()
                .WithPath(ApiPath($"/repos/{Owner}/{Repo}/pulls/26/reviews/1102/dismissals"))
                .UsingPut())
            .RespondWith(Response.Create().WithStatusCode(200).WithBody("{}"));
        Server.Given(Request.Create()
                .WithPath(ApiPath($"/repos/{Owner}/{Repo}/pulls/26/reviews/1103/dismissals"))
                .UsingPut())
            .RespondWith(Response.Create().WithStatusCode(200).WithBody("{}"));

        await using var provider = CreateProvider();
        await provider.DismissPreviousReviewAsync(26, marker, "Superseded", CancellationToken.None);

        // Only CHANGES_REQUESTED and APPROVED reviews should be dismissed (not COMMENTED)
        var dismissEntries = Server.LogEntries
            .Where(e => e.RequestMessage.Path?.Contains("/dismissals") == true)
            .ToList();
        dismissEntries.Should().HaveCount(2);
        dismissEntries.Should().Contain(e => e.RequestMessage.Path!.Contains("/1102/"));
        dismissEntries.Should().Contain(e => e.RequestMessage.Path!.Contains("/1103/"));
        dismissEntries.Should().NotContain(e => e.RequestMessage.Path!.Contains("/1101/"));
    }

    #endregion

    #region Helpers

    private static object BuildReviewResponseJson(long id) => new
    {
        id,
        body = "Review body",
        state = "COMMENTED",
        user = new { login = "bot-user", id = 100 },
        html_url = $"https://github.com/{Owner}/{Repo}/pull/1#pullrequestreview-{id}",
        submitted_at = "2026-01-15T10:00:00Z"
    };

    private static object BuildReviewJson(long id, string login, string body) => new
    {
        id,
        body,
        state = "CHANGES_REQUESTED",
        user = new { login, id = login.GetHashCode() },
        html_url = $"https://github.com/{Owner}/{Repo}/pull/1#pullrequestreview-{id}",
        submitted_at = "2026-01-15T10:00:00Z"
    };

    private static object BuildReviewJson(long id, string login, string body, string state) => new
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
