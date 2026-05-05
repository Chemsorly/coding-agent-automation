using System.Text.Json;
using WireMock.Server;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;

namespace CodingAgentWebUI.Infrastructure.UnitTests;

/// <summary>
/// Shared base class for WireMock-based HTTP-level provider tests.
/// Each test instance gets its own WireMock server on a random port.
/// Octokit prepends /api/v3 to all paths when using a non-github.com base URL,
/// so all stubs must use the <see cref="ApiPath"/> helper.
/// </summary>
public abstract class WireMockTestBase : IAsyncDisposable
{
    protected WireMockServer Server { get; }

    protected WireMockTestBase()
    {
        Server = WireMockServer.Start();
    }

    public ValueTask DisposeAsync()
    {
        Server.Stop();
        Server.Dispose();
        GC.SuppressFinalize(this);
        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// Prepends the /api/v3 prefix that Octokit adds for non-github.com base URLs.
    /// </summary>
    protected static string ApiPath(string path) => $"/api/v3{path}";

    /// <summary>Stubs a GET endpoint returning JSON with the given status code.</summary>
    protected void StubGet(string path, object responseBody, int statusCode = 200)
    {
        Server.Given(Request.Create().WithPath(path).UsingGet())
            .RespondWith(Response.Create()
                .WithStatusCode(statusCode)
                .WithHeader("Content-Type", "application/json")
                .WithBody(JsonSerializer.Serialize(responseBody, JsonOptions)));
    }

    /// <summary>Stubs a GET endpoint returning a raw string body.</summary>
    protected void StubGetRaw(string path, string body, int statusCode = 200)
    {
        Server.Given(Request.Create().WithPath(path).UsingGet())
            .RespondWith(Response.Create()
                .WithStatusCode(statusCode)
                .WithBody(body));
    }

    /// <summary>Stubs a POST endpoint returning JSON.</summary>
    protected void StubPost(string path, object responseBody, int statusCode = 200)
    {
        Server.Given(Request.Create().WithPath(path).UsingPost())
            .RespondWith(Response.Create()
                .WithStatusCode(statusCode)
                .WithHeader("Content-Type", "application/json")
                .WithBody(JsonSerializer.Serialize(responseBody, JsonOptions)));
    }

    /// <summary>Stubs a PATCH endpoint returning JSON.</summary>
    protected void StubPatch(string path, object responseBody, int statusCode = 200)
    {
        Server.Given(Request.Create().WithPath(path).UsingPatch())
            .RespondWith(Response.Create()
                .WithStatusCode(statusCode)
                .WithHeader("Content-Type", "application/json")
                .WithBody(JsonSerializer.Serialize(responseBody, JsonOptions)));
    }

    /// <summary>Stubs a DELETE endpoint.</summary>
    protected void StubDelete(string path, int statusCode = 200)
    {
        Server.Given(Request.Create().WithPath(path).UsingDelete())
            .RespondWith(Response.Create().WithStatusCode(statusCode));
    }

    // NOTE: StubError and StubRateLimited match any HTTP method. If a test stubs both a happy path
    // and an error on the same path, the last-registered stub wins for all methods. Consider adding
    // method-specific error stubs if this causes issues in more complex test scenarios.

    /// <summary>Stubs any method on a path with a given status code and optional JSON body.</summary>
    protected void StubError(string path, int statusCode, object? responseBody = null)
    {
        var response = Response.Create()
            .WithStatusCode(statusCode)
            .WithHeader("Content-Type", "application/json");

        if (responseBody is not null)
            response.WithBody(JsonSerializer.Serialize(responseBody, JsonOptions));

        Server.Given(Request.Create().WithPath(path))
            .RespondWith(response);
    }

    /// <summary>Stubs a 403 with rate limit headers. Uses a reset time in the past so the
    /// resilience pipeline falls through to default exponential backoff instead of waiting minutes.</summary>
    protected void StubRateLimited(string path)
    {
        var resetTime = DateTimeOffset.UtcNow.AddSeconds(-1).ToUnixTimeSeconds();
        Server.Given(Request.Create().WithPath(path))
            .RespondWith(Response.Create()
                .WithStatusCode(403)
                .WithHeader("Content-Type", "application/json")
                .WithHeader("X-RateLimit-Limit", "5000")
                .WithHeader("X-RateLimit-Remaining", "0")
                .WithHeader("X-RateLimit-Reset", resetTime.ToString())
                .WithBody(JsonSerializer.Serialize(new { message = "API rate limit exceeded", documentation_url = "https://docs.github.com/rest" }, JsonOptions)));
    }

    // WireMock's ILogEntry types use nullable references extensively.
    // These helpers are safe because WireMock always populates RequestMessage for logged entries.
#pragma warning disable CS8602

    /// <summary>Asserts that all requests to the server included the expected auth token.</summary>
    protected void AssertAllRequestsHaveAuthHeader(string expectedToken)
    {
        foreach (var entry in Server.LogEntries)
        {
            var authValue = GetHeaderValue(entry.RequestMessage.Headers, "Authorization");
            // Octokit uses "Token <value>" format for PAT-style credentials
            Assert.Contains($"Token {expectedToken}", authValue);
        }
    }

    /// <summary>Asserts that all requests include the expected User-Agent product header.</summary>
    protected void AssertAllRequestsHaveUserAgent(string expectedProduct)
    {
        foreach (var entry in Server.LogEntries)
        {
            var uaValue = GetHeaderValue(entry.RequestMessage.Headers, "User-Agent");
            Assert.Contains(expectedProduct, uaValue);
        }
    }

    /// <summary>Extracts a header value list as a joined string from WireMock headers.</summary>
    protected static string GetHeaderValue(IDictionary<string, WireMock.Types.WireMockList<string>>? headers, string name)
    {
        if (headers is null) return string.Empty;
        var match = headers.FirstOrDefault(h => h.Key.Equals(name, StringComparison.OrdinalIgnoreCase));
        return match.Value is not null ? string.Join(" ", match.Value) : string.Empty;
    }

    /// <summary>Gets the request body as a string for the first request matching the given path.</summary>
    protected string? GetRequestBody(string path) =>
        Server.LogEntries
            .FirstOrDefault(e => e.RequestMessage.Path?.Equals(path, StringComparison.OrdinalIgnoreCase) == true)
            ?.RequestMessage.Body;

#pragma warning restore CS8602

    /// <summary>Builds a minimal GitHub issue JSON object that Octokit can deserialize.</summary>
    protected static object BuildIssueJson(int number, string title, string? body, string[] labels) => new
    {
        id = number * 100,
        number,
        title,
        body,
        state = "open",
        user = new { login = "testuser", id = 1 },
        labels = labels.Select(l => new { id = 1, name = l, color = "ededed" }).ToArray(),
        created_at = "2026-01-01T00:00:00Z",
        updated_at = "2026-01-01T00:00:00Z"
    };

    /// <summary>Builds a minimal GitHub issue comment JSON object.</summary>
    protected static object BuildCommentJson(long id, string body, string author) => new
    {
        id,
        body,
        user = new { login = author, id = 1 },
        created_at = "2026-01-15T10:30:00Z",
        updated_at = "2026-01-15T10:30:00Z"
    };

    /// <summary>Builds a minimal GitHub label JSON object.</summary>
    protected static object BuildLabelJson(string name, string color = "ededed") => new
    {
        id = 1,
        name,
        color
    };

    /// <summary>Builds a minimal GitHub repository JSON object.</summary>
    protected static object BuildRepoJson(string owner, string repo) => new
    {
        id = 1,
        name = repo,
        full_name = $"{owner}/{repo}",
        owner = new { login = owner, id = 1 },
        @private = false,
        html_url = $"https://github.com/{owner}/{repo}"
    };

    /// <summary>Builds a minimal GitHub pull request JSON object.</summary>
    protected static object BuildPullRequestJson(int number, string htmlUrl) => new
    {
        id = number * 100,
        number,
        html_url = htmlUrl,
        state = "open",
        title = "Test PR",
        user = new { login = "testuser", id = 1 },
        head = new { @ref = "feature-branch", sha = "abc123" },
        @base = new { @ref = "main", sha = "def456" },
        created_at = "2026-01-01T00:00:00Z",
        updated_at = "2026-01-01T00:00:00Z"
    };

    /// <summary>Builds a minimal GitHub workflow run JSON object.</summary>
    protected static object BuildWorkflowRunJson(long id, string headSha, string status, string? conclusion) => new
    {
        id,
        head_sha = headSha,
        status,
        conclusion,
        html_url = $"https://github.com/owner/repo/actions/runs/{id}",
        created_at = "2026-01-01T00:00:00Z",
        updated_at = "2026-01-01T12:00:00Z"
    };

    /// <summary>Builds a minimal GitHub workflow job JSON object.</summary>
    protected static object BuildWorkflowJobJson(long id, string name, string status, string? conclusion) => new
    {
        id,
        name,
        status,
        conclusion,
        html_url = $"https://github.com/owner/repo/actions/runs/1/jobs/{id}",
        started_at = "2026-01-01T00:00:00Z",
        completed_at = conclusion is not null ? "2026-01-01T00:30:00Z" : null
    };

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };
}
