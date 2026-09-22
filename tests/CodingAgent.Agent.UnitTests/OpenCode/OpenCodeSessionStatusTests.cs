using System.Net;
using System.Text;
using System.Text.Json;
using AwesomeAssertions;
using CodingAgent.Agent.OpenCode;
using CodingAgent.Pipeline;
using Moq;
using ILogger = Serilog.ILogger;

namespace CodingAgent.Agent.UnitTests.OpenCode;

/// <summary>
/// Tests for PollAllSessionStatusesAsync, TryRefreshAllSessionStatusSummaryAsync,
/// and BuildSessionStatusSummary in OpenCodeAgentProvider.Session.cs.
///
/// These methods were previously present in OpenCodeAgentProvider.cs and are
/// now in the partial file OpenCodeAgentProvider.Session.cs. Coverage is needed
/// because the partial-class split caused SonarCloud to treat them as new lines.
/// </summary>
[Trait("Feature", "opencode-agent-executor")]
public class OpenCodeSessionStatusTests
{
    // ── TryRefreshAllSessionStatusSummaryAsync ────────────────────────────

    /// <summary>
    /// GET /session/status returns a successful response with sessions — the summary
    /// is set to a non-null string containing the count.
    /// Verified via a full ExecuteAsync call whose polling task fires before cancellation.
    /// </summary>
    [Fact]
    public async Task TryRefreshAllSessionStatusSummaryAsync_SuccessResponse_SetsSummary()
    {
        var statuses = new Dictionary<string, SseSessionStatus>
        {
            ["sess-1"] = new SseSessionStatus { Type = "busy" },
            ["sess-2"] = new SseSessionStatus { Type = "retry", Attempt = 2, Message = "rate limited" }
        };
        var statusJson = JsonSerializer.Serialize(statuses, OpenCodeJson.JsonOptions);

        var handler = new SessionStatusMockHandler(statusJson: statusJson);
        var factory = new SimpleClientFactory(handler);
        var provider = new OpenCodeAgentProvider(factory, new Mock<ILogger>().Object);

        // Drive TryRefreshAllSessionStatusSummaryAsync directly
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await provider.TryRefreshAllSessionStatusSummaryAsyncForTest(cts.Token);

        var summary = provider.AllSessionsSummaryForTest;
        summary.Should().NotBeNull("a successful /session/status response with sessions must set the summary");
        summary.Should().Contain("2 total");
    }

    /// <summary>
    /// GET /session/status returns a non-success status code — summary remains null.
    /// </summary>
    [Fact]
    public async Task TryRefreshAllSessionStatusSummaryAsync_ErrorResponse_LeavesSummaryNull()
    {
        var handler = new SessionStatusMockHandler(statusCode: HttpStatusCode.InternalServerError);
        var factory = new SimpleClientFactory(handler);
        var provider = new OpenCodeAgentProvider(factory, new Mock<ILogger>().Object);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await provider.TryRefreshAllSessionStatusSummaryAsyncForTest(cts.Token);

        provider.AllSessionsSummaryForTest.Should().BeNull(
            "a non-success /session/status response must not change the summary");
    }

    /// <summary>
    /// GET /session/status returns an empty session map — summary is set to null
    /// (the statuses is { Count: > 0 } guard in production code).
    /// </summary>
    [Fact]
    public async Task TryRefreshAllSessionStatusSummaryAsync_EmptySessionMap_ClearsSummary()
    {
        var handler = new SessionStatusMockHandler(statusJson: "{}");
        var factory = new SimpleClientFactory(handler);
        var provider = new OpenCodeAgentProvider(factory, new Mock<ILogger>().Object);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await provider.TryRefreshAllSessionStatusSummaryAsyncForTest(cts.Token);

        provider.AllSessionsSummaryForTest.Should().BeNull(
            "an empty session map must result in a null summary");
    }

    // ── BuildSessionStatusSummary ─────────────────────────────────────────

    /// <summary>
    /// All sessions are busy — summary reports total and busy counts.
    /// </summary>
    [Fact]
    public async Task BuildSessionStatusSummary_AllBusy_ReportsBusyCount()
    {
        var statuses = new Dictionary<string, SseSessionStatus>
        {
            ["s1"] = new SseSessionStatus { Type = "busy" },
            ["s2"] = new SseSessionStatus { Type = "busy" }
        };
        var json = JsonSerializer.Serialize(statuses, OpenCodeJson.JsonOptions);

        var handler = new SessionStatusMockHandler(statusJson: json);
        var factory = new SimpleClientFactory(handler);
        var provider = new OpenCodeAgentProvider(factory, new Mock<ILogger>().Object);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await provider.TryRefreshAllSessionStatusSummaryAsyncForTest(cts.Token);

        var summary = provider.AllSessionsSummaryForTest;
        summary.Should().Contain("2 total");
        summary.Should().Contain("2 busy");
        summary.Should().NotContain("retrying");
        summary.Should().NotContain("idle");
    }

    /// <summary>
    /// All sessions are idle — summary reports idle count only.
    /// </summary>
    [Fact]
    public async Task BuildSessionStatusSummary_AllIdle_ReportsIdleCount()
    {
        var statuses = new Dictionary<string, SseSessionStatus>
        {
            ["s1"] = new SseSessionStatus { Type = "idle" },
            ["s2"] = new SseSessionStatus { Type = "idle" },
            ["s3"] = new SseSessionStatus { Type = "idle" }
        };
        var json = JsonSerializer.Serialize(statuses, OpenCodeJson.JsonOptions);

        var handler = new SessionStatusMockHandler(statusJson: json);
        var factory = new SimpleClientFactory(handler);
        var provider = new OpenCodeAgentProvider(factory, new Mock<ILogger>().Object);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await provider.TryRefreshAllSessionStatusSummaryAsyncForTest(cts.Token);

        var summary = provider.AllSessionsSummaryForTest;
        summary.Should().Contain("3 total");
        summary.Should().Contain("3 idle");
        summary.Should().NotContain("busy");
        summary.Should().NotContain("retrying");
    }

    /// <summary>
    /// Mix of retry, busy, idle sessions with retry details (≤3 retrying sessions)
    /// — summary contains all counts and retry detail lines.
    /// </summary>
    [Fact]
    public async Task BuildSessionStatusSummary_MixedTypes_IncludesAllCountsAndRetryDetail()
    {
        var statuses = new Dictionary<string, SseSessionStatus>
        {
            ["s1"] = new SseSessionStatus { Type = "retry", Attempt = 1, Message = "rate limit" },
            ["s2"] = new SseSessionStatus { Type = "retry", Attempt = 3, Message = "timeout" },
            ["s3"] = new SseSessionStatus { Type = "busy" },
            ["s4"] = new SseSessionStatus { Type = "idle" }
        };
        var json = JsonSerializer.Serialize(statuses, OpenCodeJson.JsonOptions);

        var handler = new SessionStatusMockHandler(statusJson: json);
        var factory = new SimpleClientFactory(handler);
        var provider = new OpenCodeAgentProvider(factory, new Mock<ILogger>().Object);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await provider.TryRefreshAllSessionStatusSummaryAsyncForTest(cts.Token);

        var summary = provider.AllSessionsSummaryForTest;
        summary.Should().Contain("4 total");
        summary.Should().Contain("2 retrying");
        summary.Should().Contain("1 busy");
        summary.Should().Contain("1 idle");
        summary.Should().Contain("detail:");
    }

    /// <summary>
    /// More than 3 retrying sessions — only 3 detail entries are included (Take(3) guard).
    /// </summary>
    [Fact]
    public async Task BuildSessionStatusSummary_ManyRetryingSessions_DetailTruncatedToThree()
    {
        var statuses = Enumerable.Range(1, 5)
            .ToDictionary(i => $"s{i}", i => new SseSessionStatus
            {
                Type = "retry",
                Attempt = i,
                Message = $"error {i}"
            });
        var json = JsonSerializer.Serialize(statuses, OpenCodeJson.JsonOptions);

        var handler = new SessionStatusMockHandler(statusJson: json);
        var factory = new SimpleClientFactory(handler);
        var provider = new OpenCodeAgentProvider(factory, new Mock<ILogger>().Object);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await provider.TryRefreshAllSessionStatusSummaryAsyncForTest(cts.Token);

        var summary = provider.AllSessionsSummaryForTest!;
        // Count "attempt" occurrences — should be exactly 3 from the Take(3)
        var attemptCount = summary.Split("attempt", StringSplitOptions.None).Length - 1;
        attemptCount.Should().Be(3, "Take(3) must limit retry detail lines to 3");
    }

    /// <summary>
    /// Retry session with null Message — detail uses "unknown" as fallback.
    /// </summary>
    [Fact]
    public async Task BuildSessionStatusSummary_RetryWithNullMessage_UsesUnknownFallback()
    {
        var statuses = new Dictionary<string, SseSessionStatus>
        {
            ["s1"] = new SseSessionStatus { Type = "retry", Attempt = 1, Message = null }
        };
        var json = JsonSerializer.Serialize(statuses, OpenCodeJson.JsonOptions);

        var handler = new SessionStatusMockHandler(statusJson: json);
        var factory = new SimpleClientFactory(handler);
        var provider = new OpenCodeAgentProvider(factory, new Mock<ILogger>().Object);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await provider.TryRefreshAllSessionStatusSummaryAsyncForTest(cts.Token);

        var summary = provider.AllSessionsSummaryForTest!;
        summary.Should().Contain("unknown", "null Message must fall back to 'unknown'");
    }

    // ── PollAllSessionStatusesAsync ───────────────────────────────────────

    /// <summary>
    /// PollAllSessionStatusesAsync exits immediately when the token is already cancelled
    /// before the initial Task.Delay(2000) completes.
    /// </summary>
    [Fact]
    public async Task PollAllSessionStatusesAsync_CancelledImmediately_ExitsCleanly()
    {
        var handler = new SessionStatusMockHandler(statusJson: "{}");
        var factory = new SimpleClientFactory(handler);
        var provider = new OpenCodeAgentProvider(factory, new Mock<ILogger>().Object);

        using var cts = new CancellationTokenSource();
        cts.Cancel(); // Pre-cancel

        // Should not throw and should exit quickly
        await provider.PollAllSessionStatusesAsyncForTest(cts.Token);

        // No /session/status call should have been made
        handler.SessionStatusCallCount.Should().Be(0,
            "pre-cancelled token must exit before the first poll cycle");
    }

    /// <summary>
    /// PollAllSessionStatusesAsync makes at least one /session/status call before
    /// the token is cancelled mid-loop (cancel after first poll fires).
    /// Uses a very short initial delay to keep the test fast.
    /// </summary>
    [Fact]
    public async Task PollAllSessionStatusesAsync_RunsAtLeastOneCycle_BeforeCancellation()
    {
        var statuses = new Dictionary<string, SseSessionStatus>
        {
            ["s1"] = new SseSessionStatus { Type = "idle" }
        };
        var statusJson = JsonSerializer.Serialize(statuses, OpenCodeJson.JsonOptions);

        var handler = new SessionStatusMockHandler(statusJson: statusJson, repeatResponse: true);
        var factory = new SimpleClientFactory(handler);
        var provider = new OpenCodeAgentProvider(factory, new Mock<ILogger>().Object);

        // Cancel after enough time for one poll cycle to run (with fast initial delay override).
        // Use 2000 ms instead of 200 ms to prevent flakiness under CI load — the initial delay
        // is only 10 ms, so 2000 ms still guarantees cancellation fires after at least one cycle
        // while giving the mock HTTP handler ample scheduling time to return.
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(2000));

        await provider.PollAllSessionStatusesAsyncForTest(cts.Token, initialDelayMs: 10);

        handler.SessionStatusCallCount.Should().BeGreaterThan(0,
            "at least one /session/status call must have been made before cancellation");
    }

    // ── Infrastructure ────────────────────────────────────────────────────

    private sealed class SessionStatusMockHandler : HttpMessageHandler
    {
        private readonly string _statusJson;
        private readonly HttpStatusCode _statusCode;
        private readonly bool _repeatResponse;
        private int _sessionStatusCallCount;

        public int SessionStatusCallCount => _sessionStatusCallCount;

        public SessionStatusMockHandler(
            string statusJson = "{}",
            HttpStatusCode statusCode = HttpStatusCode.OK,
            bool repeatResponse = false)
        {
            _statusJson = statusJson;
            _statusCode = statusCode;
            _repeatResponse = repeatResponse;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri?.PathAndQuery ?? "";

            if (path.Contains("/session/status"))
            {
                Interlocked.Increment(ref _sessionStatusCallCount);
                var response = new HttpResponseMessage(_statusCode)
                {
                    Content = new StringContent(_statusJson, Encoding.UTF8, "application/json")
                };
                return Task.FromResult(response);
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }

    private sealed class SimpleClientFactory : IHttpClientFactory
    {
        private readonly HttpMessageHandler _handler;

        public SimpleClientFactory(HttpMessageHandler handler)
        {
            _handler = handler;
        }

        public HttpClient CreateClient(string name)
        {
            return new HttpClient(_handler, disposeHandler: false)
            {
                BaseAddress = new Uri(AgentDefaults.OpenCodeBaseUrl)
            };
        }
    }
}
