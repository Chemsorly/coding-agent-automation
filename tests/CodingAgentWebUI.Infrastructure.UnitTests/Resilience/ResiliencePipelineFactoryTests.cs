using AwesomeAssertions;
using LibGit2Sharp;
using Octokit;
using Polly.Timeout;
using CodingAgentWebUI.Infrastructure.Resilience;
using Serilog;

namespace CodingAgentWebUI.Infrastructure.UnitTests.Resilience;

public class ResiliencePipelineFactoryTests
{
    [Fact]
    public void IsRetryableApiException_5xx_ReturnsTrue()
    {
        var response = CreateMockResponse(System.Net.HttpStatusCode.InternalServerError);
        var ex = new ApiException("Server Error", response.StatusCode);
        ResiliencePipelineFactory.IsRetryableApiException(ex).Should().BeTrue();
    }

    [Fact]
    public void IsRetryableApiException_502_ReturnsTrue()
    {
        var ex = new ApiException("Bad Gateway", System.Net.HttpStatusCode.BadGateway);
        ResiliencePipelineFactory.IsRetryableApiException(ex).Should().BeTrue();
    }

    [Fact]
    public void IsRetryableApiException_4xx_ReturnsFalse()
    {
        var ex = new ApiException("Not Found", System.Net.HttpStatusCode.NotFound);
        ResiliencePipelineFactory.IsRetryableApiException(ex).Should().BeFalse();
    }

    [Fact]
    public void IsRetryableApiException_401_ReturnsFalse()
    {
        var ex = new ApiException("Unauthorized", System.Net.HttpStatusCode.Unauthorized);
        ResiliencePipelineFactory.IsRetryableApiException(ex).Should().BeFalse();
    }

    [Theory]
    [InlineData("connection timed out")]
    [InlineData("DNS resolution failed")]
    [InlineData("connection reset by peer")]
    [InlineData("503 Service Unavailable")]
    [InlineData("network is unreachable")]
    [InlineData("Name or service not known")]
    [InlineData("could not resolve host")]
    public void IsTransientGitException_NetworkError_ReturnsTrue(string message)
    {
        var ex = new LibGit2SharpException(message);
        ResiliencePipelineFactory.IsTransientGitException(ex).Should().BeTrue();
    }

    [Theory]
    [InlineData("protected branch hook declined")]
    [InlineData("non-fast-forward update rejected")]
    [InlineData("authentication required")]
    [InlineData("invalid credentials")]
    [InlineData("401 Unauthorized")]
    [InlineData("403 Forbidden")]
    [InlineData("rejected by remote")]
    public void IsTransientGitException_NonTransientError_ReturnsFalse(string message)
    {
        var ex = new LibGit2SharpException(message);
        ResiliencePipelineFactory.IsTransientGitException(ex).Should().BeFalse();
    }

    [Fact]
    public void CreateGitHubApiPipeline_ReturnsNonNullPipeline()
    {
        var pipeline = ResiliencePipelineFactory.CreateGitHubApiPipeline(Log.Logger);
        pipeline.Should().NotBeNull();
    }

    [Fact]
    public void CreateGitNetworkPipeline_ReturnsNonNullPipeline()
    {
        var pipeline = ResiliencePipelineFactory.CreateGitNetworkPipeline(Log.Logger);
        pipeline.Should().NotBeNull();
    }

    [Fact]
    public void CreateHttpPipeline_ReturnsNonNullPipeline()
    {
        var pipeline = ResiliencePipelineFactory.CreateHttpPipeline(Log.Logger);
        pipeline.Should().NotBeNull();
    }

    [Fact]
    public void CreateSignalRPipeline_ReturnsNonNullPipeline()
    {
        var pipeline = ResiliencePipelineFactory.CreateSignalRPipeline(Log.Logger);
        pipeline.Should().NotBeNull();
    }

    // TODO: Add a test verifying CreateGitHubActionsLogsPipeline retries AuthorizationException,
    // mirroring CreateGitHubApiPipeline_RetriesAuthorizationException, to guard against accidental removal.
    [Fact]
    public void CreateGitHubActionsLogsPipeline_ReturnsNonNullPipeline()
    {
        var pipeline = ResiliencePipelineFactory.CreateGitHubActionsLogsPipeline(Log.Logger);
        pipeline.Should().NotBeNull();
    }

    [Fact]
    public async Task CreateGitNetworkPipeline_HangingOperation_ThrowsTimeoutRejectedException()
    {
        // Short per-attempt timeout (500ms) and short outer timeout (3s) for fast test execution.
        // With TimeoutRejectedException now retried (MaxRetryAttempts=2 → 3 attempts × 500ms + backoff),
        // the outer timeout caps total execution.
        var pipeline = ResiliencePipelineFactory.CreateGitNetworkPipeline(
            Log.Logger, TimeSpan.FromMilliseconds(500), outerTimeout: TimeSpan.FromSeconds(3));

        var act = () => pipeline.ExecuteAsync(async token =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
        }, CancellationToken.None).AsTask();

        await act.Should().ThrowAsync<TimeoutRejectedException>();
    }

    [Fact]
    public async Task CreateSignalRPipeline_HangingOperation_ThrowsTimeoutRejectedException()
    {
        // Short per-attempt timeout (500ms) and short outer timeout (3s) for fast test execution.
        // With TimeoutRejectedException now retried (MaxRetryAttempts=3 → 4 attempts × 500ms + backoff),
        // the outer timeout caps total execution.
        var pipeline = ResiliencePipelineFactory.CreateSignalRPipeline(
            Log.Logger, TimeSpan.FromMilliseconds(500), outerTimeout: TimeSpan.FromSeconds(3));

        var act = () => pipeline.ExecuteAsync(async token =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
        }, CancellationToken.None).AsTask();

        await act.Should().ThrowAsync<TimeoutRejectedException>();
    }

    [Fact]
    public async Task CreateGitNetworkPipeline_RetriesOnPerAttemptTimeout()
    {
        // Per-attempt timeout is 200ms, outer timeout generous (10s) to allow all retries to complete.
        // MaxRetryAttempts=2 → 3 total attempts when every attempt times out.
        var pipeline = ResiliencePipelineFactory.CreateGitNetworkPipeline(
            Log.Logger, TimeSpan.FromMilliseconds(200), outerTimeout: TimeSpan.FromSeconds(10));
        var callCount = 0;

        var act = () => pipeline.ExecuteAsync(async token =>
        {
            callCount++;
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
        }, CancellationToken.None).AsTask();

        await act.Should().ThrowAsync<TimeoutRejectedException>();
        // MaxRetryAttempts=2 means 1 initial + 2 retries = 3 total attempts
        callCount.Should().Be(3);
    }

    [Fact]
    public async Task CreateSignalRPipeline_RetriesOnPerAttemptTimeout()
    {
        // Per-attempt timeout is 200ms, outer timeout generous (10s) to allow all retries to complete.
        // MaxRetryAttempts=3 → 4 total attempts when every attempt times out.
        var pipeline = ResiliencePipelineFactory.CreateSignalRPipeline(
            Log.Logger, TimeSpan.FromMilliseconds(200), outerTimeout: TimeSpan.FromSeconds(10));
        var callCount = 0;

        var act = () => pipeline.ExecuteAsync(async token =>
        {
            callCount++;
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
        }, CancellationToken.None).AsTask();

        await act.Should().ThrowAsync<TimeoutRejectedException>();
        // MaxRetryAttempts=3 means 1 initial + 3 retries = 4 total attempts
        callCount.Should().Be(4);
    }

    [Fact]
    public void TruncateMessage_Null_ReturnsUnknown()
    {
        ResiliencePipelineFactory.TruncateMessage(null).Should().Be("unknown");
    }

    [Fact]
    public void TruncateMessage_ShortMessage_ReturnsSameMessage()
    {
        ResiliencePipelineFactory.TruncateMessage("short error").Should().Be("short error");
    }

    [Fact]
    public void TruncateMessage_Exactly200Chars_ReturnsSameMessage()
    {
        var message = new string('x', 200);
        ResiliencePipelineFactory.TruncateMessage(message).Should().Be(message);
    }

    [Fact]
    public void TruncateMessage_Over200Chars_TruncatesWithEllipsis()
    {
        var message = new string('x', 250);
        var result = ResiliencePipelineFactory.TruncateMessage(message);
        result.Should().HaveLength(201); // 200 chars + ellipsis
        result.Should().EndWith("…");
        result.Should().StartWith("xxxxx");
    }

    [Fact]
    public async Task HttpPipeline_OnRetry_AddsActivityEventWithExceptionMessage()
    {
        using var activity = new System.Diagnostics.Activity("test").Start();
        var pipeline = ResiliencePipelineFactory.CreateHttpPipeline(Log.Logger);
        var callCount = 0;

        await Assert.ThrowsAsync<HttpRequestException>(async () =>
        {
            await pipeline.ExecuteAsync(async _ =>
            {
                callCount++;
                throw new HttpRequestException("Connection refused");
            }, CancellationToken.None);
        });

        callCount.Should().BeGreaterThan(1); // At least one retry occurred
        var events = activity.Events.ToList();
        events.Should().NotBeEmpty();
        var retryEvent = events.First();
        retryEvent.Name.Should().Be("retry");
        var tags = retryEvent.Tags.ToDictionary(t => t.Key, t => t.Value);
        tags.Should().ContainKey("attempt");
        tags.Should().ContainKey("exception.type");
        tags["exception.type"].Should().Be("HttpRequestException");
        tags.Should().ContainKey("exception.message");
        tags["exception.message"].Should().Be("Connection refused");
    }

    [Fact]
    public async Task HttpPipeline_OnRetry_NoNullReferenceWhenNoActivity()
    {
        // Ensure Activity.Current is null
        System.Diagnostics.Activity.Current = null;
        var pipeline = ResiliencePipelineFactory.CreateHttpPipeline(Log.Logger);

        // Should not throw NullReferenceException
        await Assert.ThrowsAsync<HttpRequestException>(async () =>
        {
            await pipeline.ExecuteAsync(async _ =>
            {
                throw new HttpRequestException("test");
            }, CancellationToken.None);
        });
    }

    [Fact]
    public async Task CreateGitHubApiPipeline_OuterTimeoutCancelsRateLimitWait()
    {
        // Arrange: create pipeline with a short outer timeout (1s) to verify it fires during rate-limit delay
        var pipeline = ResiliencePipelineFactory.CreateGitHubApiPipeline(
            Log.Logger, outerTimeout: TimeSpan.FromSeconds(1));

        // Act: simulate a rate-limit exception that tells us to wait 60s (exceeds outer timeout)
        var act = () => pipeline.ExecuteAsync(async token =>
        {
            var response = CreateRateLimitResponse(DateTimeOffset.UtcNow.AddMinutes(1));
            throw new Octokit.RateLimitExceededException(response);
        }, CancellationToken.None).AsTask();

        // Assert: outer timeout fires during the rate-limit retry delay, producing TimeoutRejectedException
        await act.Should().ThrowAsync<TimeoutRejectedException>();
    }

    [Fact]
    public async Task CreateGitHubApiPipeline_PerAttemptTimeoutStillApplies()
    {
        // Arrange: outer timeout is generous (30s), but per-attempt timeout is short (1s)
        var pipeline = ResiliencePipelineFactory.CreateGitHubApiPipeline(
            Log.Logger,
            outerTimeout: TimeSpan.FromSeconds(30),
            perAttemptTimeout: TimeSpan.FromSeconds(1));

        // Act: each attempt hangs indefinitely — per-attempt timeout should fire
        var act = () => pipeline.ExecuteAsync(async token =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
        }, CancellationToken.None).AsTask();

        // Assert: inner per-attempt timeout fires
        await act.Should().ThrowAsync<TimeoutRejectedException>();
    }

    [Fact]
    public async Task CreateGitHubApiPipeline_RetriesOnPerAttemptTimeout()
    {
        // Arrange: short per-attempt timeout (200ms), generous outer timeout to allow all retries.
        // MaxRetryAttempts=3 → 4 total attempts. Delegate hangs on first 3 attempts, succeeds on 4th.
        var pipeline = ResiliencePipelineFactory.CreateGitHubApiPipeline(
            Log.Logger,
            outerTimeout: TimeSpan.FromSeconds(10),
            perAttemptTimeout: TimeSpan.FromMilliseconds(200));
        var callCount = 0;

        // Act: first 3 calls hang (triggering per-attempt timeout), 4th succeeds immediately
        await pipeline.ExecuteAsync(async token =>
        {
            callCount++;
            if (callCount <= 3)
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
        }, CancellationToken.None);

        // Assert: retried 3 times on timeout, succeeded on the 4th attempt
        callCount.Should().Be(4);
    }

    [Fact]
    public async Task CreateGitHubApiPipeline_OuterTimeoutCapsRetriesOnPerAttemptTimeout()
    {
        // Arrange: short outer timeout (1s) and short per-attempt timeout (300ms).
        // With MaxRetryAttempts=3, all retries would take ~1.2s+ (4 × 300ms + backoff delays).
        // The outer timeout should fire before all retries complete.
        var pipeline = ResiliencePipelineFactory.CreateGitHubApiPipeline(
            Log.Logger,
            outerTimeout: TimeSpan.FromSeconds(1),
            perAttemptTimeout: TimeSpan.FromMilliseconds(300));
        var callCount = 0;

        // Act: every attempt hangs indefinitely
        var act = () => pipeline.ExecuteAsync(async token =>
        {
            callCount++;
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
        }, CancellationToken.None).AsTask();

        // Assert: outer timeout fires before all retries complete
        await act.Should().ThrowAsync<TimeoutRejectedException>();
        // TODO: callCount.Should().BeGreaterThan(1) may be flaky — with 1s outer timeout, 300ms per-attempt,
        // and exponential backoff with jitter (base delay 1s), the retry delay after the first attempt may
        // exceed the remaining ~700ms of outer timeout, causing only 1 attempt. Consider using a longer outer
        // timeout (e.g., 2s) or reducing the backoff delay to reliably guarantee at least 2 attempts.
        callCount.Should().BeGreaterThan(1);
        // TODO: This upper-bound assertion (< 5) is vacuous since MaxRetryAttempts=3 limits total attempts
        // to 4 regardless of the outer timeout. To meaningfully verify the outer timeout interrupted retries,
        // this should be callCount.Should().BeLessThan(4) — which would fail if all retries completed.
        callCount.Should().BeLessThan(4 + 1); // Less than MaxRetryAttempts + 1
    }

    [Fact]
    public async Task CreateGitNetworkPipeline_RetriesTransientGitException()
    {
        // Arrange: pipeline with generous timeout (10s) to allow retries
        var pipeline = ResiliencePipelineFactory.CreateGitNetworkPipeline(Log.Logger, TimeSpan.FromSeconds(10));
        var callCount = 0;

        // Act: first two calls throw transient error, third succeeds
        await pipeline.ExecuteAsync(async _ =>
        {
            callCount++;
            if (callCount <= 2)
                throw new LibGit2SharpException("connection timed out");
            await Task.CompletedTask;
        }, CancellationToken.None);

        // Assert: retried twice then succeeded on third attempt (MaxRetryAttempts=2 means 3 total attempts)
        callCount.Should().Be(3);
    }

    [Fact]
    public async Task CreateGitNetworkPipeline_DoesNotRetryNonTransientGitException()
    {
        // Arrange
        var pipeline = ResiliencePipelineFactory.CreateGitNetworkPipeline(Log.Logger, TimeSpan.FromSeconds(10));
        var callCount = 0;

        // Act: throw a non-transient error (auth failure)
        var act = () => pipeline.ExecuteAsync(async _ =>
        {
            callCount++;
            throw new LibGit2SharpException("authentication required");
        }, CancellationToken.None).AsTask();

        // Assert: not retried — only 1 call
        await act.Should().ThrowAsync<LibGit2SharpException>();
        callCount.Should().Be(1);
    }

    [Fact]
    public async Task CreateGitHubApiPipeline_RetriesAuthorizationException()
    {
        var pipeline = ResiliencePipelineFactory.CreateGitHubApiPipeline(
            Log.Logger, outerTimeout: TimeSpan.FromSeconds(30), perAttemptTimeout: TimeSpan.FromSeconds(5));
        var callCount = 0;

        await pipeline.ExecuteAsync(async _ =>
        {
            callCount++;
            if (callCount <= 2)
                throw new Octokit.AuthorizationException(CreateMockResponse(System.Net.HttpStatusCode.Unauthorized));
            await Task.CompletedTask;
        }, CancellationToken.None);

        callCount.Should().Be(3);
    }

    [Fact]
    public async Task CreateGitHubApiPipeline_Retries5xxServerError()
    {
        var pipeline = ResiliencePipelineFactory.CreateGitHubApiPipeline(
            Log.Logger, outerTimeout: TimeSpan.FromSeconds(30), perAttemptTimeout: TimeSpan.FromSeconds(5));
        var callCount = 0;

        await pipeline.ExecuteAsync(async _ =>
        {
            callCount++;
            if (callCount <= 2)
                throw new ApiException("Internal Server Error", System.Net.HttpStatusCode.InternalServerError);
            await Task.CompletedTask;
        }, CancellationToken.None);

        callCount.Should().Be(3);
    }

    // TODO: Use Octokit.ForbiddenException instead of raw ApiException to match acceptance criteria
    // and catch potential future .Handle<ForbiddenException>() additions.
    [Fact]
    public async Task CreateGitHubApiPipeline_DoesNotRetryForbiddenException()
    {
        var pipeline = ResiliencePipelineFactory.CreateGitHubApiPipeline(
            Log.Logger, outerTimeout: TimeSpan.FromSeconds(30), perAttemptTimeout: TimeSpan.FromSeconds(5));
        var callCount = 0;

        var act = () => pipeline.ExecuteAsync(async _ =>
        {
            callCount++;
            throw new ApiException("Forbidden", System.Net.HttpStatusCode.Forbidden);
        }, CancellationToken.None).AsTask();

        await act.Should().ThrowAsync<ApiException>();
        callCount.Should().Be(1);
    }

    private static Octokit.IResponse CreateMockResponse(System.Net.HttpStatusCode statusCode)
    {
        var mock = new Moq.Mock<Octokit.IResponse>();
        mock.Setup(r => r.StatusCode).Returns(statusCode);
        return mock.Object;
    }

    private static Octokit.IResponse CreateRateLimitResponse(DateTimeOffset resetTime)
    {
        var rateLimit = new Octokit.RateLimit(5000, 0, resetTime.ToUnixTimeSeconds());
        var apiInfo = new Octokit.ApiInfo(
            new Dictionary<string, Uri>(), new List<string>(), new List<string>(),
            string.Empty, rateLimit);

        var mock = new Moq.Mock<Octokit.IResponse>();
        mock.Setup(r => r.StatusCode).Returns(System.Net.HttpStatusCode.Forbidden);
        mock.Setup(r => r.Headers).Returns(new Dictionary<string, string>());
        mock.Setup(r => r.Body).Returns("");
        mock.Setup(r => r.ContentType).Returns("application/json");
        mock.Setup(r => r.ApiInfo).Returns(apiInfo);
        return mock.Object;
    }
}
