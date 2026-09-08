using AwesomeAssertions;
using CodingAgentWebUI.Infrastructure.GitHub;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using Xunit;

namespace CodingAgentWebUI.Infrastructure.UnitTests.GitHub;

/// <summary>
/// Tests for <see cref="GitHubRepositoryProvider.ExtractLinkedIssuesAsync"/> failure paths:
/// <list type="bullet">
/// <item>GetPr failure does not propagate</item>
/// <item>Timeline results still returned when GetPr fails</item>
/// <item>Warning is logged when GetPr fails</item>
/// <item>OperationCanceledException propagates (is not swallowed)</item>
/// </list>
/// Placed in the <c>StaticLogger</c> xUnit collection because the warning-log test temporarily
/// replaces the global <see cref="Log.Logger"/> — serialisation prevents races with other
/// classes that also mutate the static logger.
/// </summary>
[Collection("StaticLogger")]
public class GitHubRepositoryProviderExtractLinkedIssuesFailureTests
    : WireMockTestBase, IDisposable
{
    private const string Owner = "test-owner";
    private const string Repo = "test-repo";
    private const string Token = "fake-token-99";
    private const string BaseBranch = "main";

    // Logger infrastructure — installed in constructor, restored in Dispose.
    private readonly CollectingSink _sink;
    private readonly ILogger _previousLogger;

    public GitHubRepositoryProviderExtractLinkedIssuesFailureTests()
    {
        _previousLogger = Log.Logger;
        _sink = new CollectingSink();
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Verbose()
            .WriteTo.Sink(_sink)
            .CreateLogger();
    }

    void IDisposable.Dispose()
    {
        Log.Logger = _previousLogger;
        // TODO [WARNING]: sync-over-async on a ValueTask. WireMockTestBase.DisposeAsync is currently
        // synchronous so this is safe today, but if it becomes truly async this .GetResult() call will
        // deadlock on a thread-pool thread (xUnit executes Dispose on thread-pool threads). Refactor
        // this class to implement IAsyncDisposable and use `await using`, or call Server.Stop()/Server.Dispose()
        // directly (both are synchronous) to eliminate the fragile pattern.
        // WireMockTestBase.DisposeAsync stops and disposes the WireMock server.
        DisposeAsync().AsTask().GetAwaiter().GetResult();
    }

    private GitHubRepositoryProvider CreateProvider() =>
        new(new GitHubConnectionInfo(Server.Url!, Owner, Repo), Token, BaseBranch);

    // ── AC1: failure does not propagate, returns empty ────────────────────────

    [Fact]
    public async Task ExtractLinkedIssuesAsync_GetPrFailure_DoesNotPropagateAndReturnsEmpty()
    {
        // Arrange: timeline returns nothing, GetPr returns 500.
        StubGet(ApiPath($"/repos/{Owner}/{Repo}/issues/88/timeline"), Array.Empty<object>());
        StubError(ApiPath($"/repos/{Owner}/{Repo}/pulls/88"), 500, new { message = "Internal Server Error" });

        await using var provider = CreateProvider();

        // Act + Assert: must not throw.
        var result = await provider.ExtractLinkedIssuesAsync(88, CancellationToken.None);

        result.Should().BeEmpty("GetPr failure with empty timeline must return empty list, not throw");
    }

    // ── AC3: timeline results still returned when GetPr fails ─────────────────

    [Fact]
    public async Task ExtractLinkedIssuesAsync_GetPrFailure_TimelineResultsStillReturned()
    {
        // Arrange: timeline returns a cross-reference to issue #42; GetPr returns 500.
        StubGet(ApiPath($"/repos/{Owner}/{Repo}/issues/89/timeline"),
            new[] { BuildCrossRefTimelineEvent(issueNumber: 42, isPullRequest: false) });
        StubError(ApiPath($"/repos/{Owner}/{Repo}/pulls/89"), 500, new { message = "Internal Server Error" });

        await using var provider = CreateProvider();

        // Act
        var result = await provider.ExtractLinkedIssuesAsync(89, CancellationToken.None);

        // Assert: timeline-discovered issue must survive GetPr failure.
        // TODO [WARNING]: assertion is weaker than necessary — use .BeEquivalentTo(new[] { "42" })
        // to also verify no unexpected entries were added alongside the timeline result.
        result.Should().Contain("42",
            "linked issues found via timeline API must be returned even when GetPr fails");
    }

    // ── AC2: warning is logged when GetPr fails ───────────────────────────────

    [Fact]
    public async Task ExtractLinkedIssuesAsync_GetPrFailure_WarningLogged()
    {
        // Arrange
        StubGet(ApiPath($"/repos/{Owner}/{Repo}/issues/90/timeline"), Array.Empty<object>());
        StubError(ApiPath($"/repos/{Owner}/{Repo}/pulls/90"), 500, new { message = "Internal Server Error" });

        _sink.Clear();

        await using var provider = CreateProvider();

        // Act
        await provider.ExtractLinkedIssuesAsync(90, CancellationToken.None);

        // Assert: at least one Warning-level event must have been emitted.
        // TODO [WARNING]: assertion is too weak — it accepts any Warning from any code path. Tighten
        // to also verify the event is specifically about the GetPr failure, e.g.:
        //   _sink.Events.Should().Contain(e =>
        //       e.Level == LogEventLevel.Warning &&
        //       e.MessageTemplate.Text.Contains("Failed to fetch PR metadata"));
        // As-is, an unrelated Warning emitted by another code path would satisfy this assertion.
        _sink.Events.Should().Contain(e =>
            e.Level == LogEventLevel.Warning,
            "a Warning must be logged when GetPr fails");
    }

    // ── OperationCanceledException propagates ─────────────────────────────────

    [Fact]
    public async Task ExtractLinkedIssuesAsync_Cancelled_PropagatesOperationCanceledException()
    {
        // TODO [WARNING]: this test uses a pre-cancelled token, so cancellation is observed by the
        // *timeline* call (first async operation), not the new GetPr catch block. The test validates
        // the method-boundary contract but does NOT independently exercise the `when (ex is not
        // OperationCanceledException)` filter on the GetPr catch. To close this gap, add a variant
        // where the timeline stub returns a valid response and the token is cancelled just before
        // the GetPr call (e.g., via a WireMock delay + CancellationTokenSource.CancelAfter).
        // Arrange: use a pre-cancelled token so the very first async operation (timeline or GetPr)
        // observes cancellation without needing to race with the HTTP call.
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await using var provider = CreateProvider();

        // Act + Assert: OperationCanceledException (base type) must propagate.
        await provider
            .Invoking(p => p.ExtractLinkedIssuesAsync(91, cts.Token))
            .Should()
            .ThrowAsync<OperationCanceledException>(
                "OperationCanceledException must not be swallowed by the catch filter");
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Builds a timeline event JSON object representing a <c>crossreferenced</c> event.
    /// Mirrors the helper in <see cref="GitHubRepositoryProviderPullRequestMethodsTests"/>.
    /// </summary>
    private static object BuildCrossRefTimelineEvent(int issueNumber, bool isPullRequest)
    {
        object sourceIssue = isPullRequest
            ? new
            {
                number = issueNumber,
                title = $"PR or Issue #{issueNumber}",
                state = "open",
                pull_request = new { html_url = $"https://github.com/test-owner/test-repo/pull/{issueNumber}" }
            }
            : new
            {
                number = issueNumber,
                title = $"Issue #{issueNumber}",
                state = "open"
                // No pull_request field — Octokit deserialises PullRequest as null
            };

        return new
        {
            @event = "cross-referenced",
            source = new
            {
                type = "issue",
                issue = sourceIssue
            }
        };
    }

    /// <summary>
    /// Simple sink that collects log events for assertion.
    /// Thread-safe: Emit/Clear are locked to prevent concurrent modification.
    /// </summary>
    private sealed class CollectingSink : ILogEventSink
    {
        private readonly object _lock = new();
        private readonly List<LogEvent> _events = new();

        /// <summary>Returns a snapshot of all collected events, safe to enumerate after the call.</summary>
        public IReadOnlyList<LogEvent> Events { get { lock (_lock) { return _events.ToList(); } } }

        public void Emit(LogEvent logEvent) { lock (_lock) { _events.Add(logEvent); } }
        public void Clear() { lock (_lock) { _events.Clear(); } }
    }
}
