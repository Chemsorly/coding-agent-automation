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

    [Fact]
    public void CreateGitHubActionsLogsPipeline_ReturnsNonNullPipeline()
    {
        var pipeline = ResiliencePipelineFactory.CreateGitHubActionsLogsPipeline(Log.Logger);
        pipeline.Should().NotBeNull();
    }

    [Fact]
    public async Task CreateGitNetworkPipeline_HangingOperation_ThrowsTimeoutRejectedException()
    {
        var pipeline = ResiliencePipelineFactory.CreateGitNetworkPipeline(Log.Logger, TimeSpan.FromSeconds(1));

        var act = () => pipeline.ExecuteAsync(async token =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
        }, CancellationToken.None).AsTask();

        await act.Should().ThrowAsync<TimeoutRejectedException>();
    }

    [Fact]
    public async Task CreateSignalRPipeline_HangingOperation_ThrowsTimeoutRejectedException()
    {
        var pipeline = ResiliencePipelineFactory.CreateSignalRPipeline(Log.Logger, TimeSpan.FromSeconds(1));

        var act = () => pipeline.ExecuteAsync(async token =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
        }, CancellationToken.None).AsTask();

        await act.Should().ThrowAsync<TimeoutRejectedException>();
    }

    [Fact]
    public async Task CreateGitNetworkPipeline_TimeoutExceptionNotRetried()
    {
        var pipeline = ResiliencePipelineFactory.CreateGitNetworkPipeline(Log.Logger, TimeSpan.FromSeconds(1));
        var callCount = 0;

        var act = () => pipeline.ExecuteAsync(async token =>
        {
            callCount++;
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
        }, CancellationToken.None).AsTask();

        await act.Should().ThrowAsync<TimeoutRejectedException>();
        callCount.Should().Be(1);
    }

    [Fact]
    public async Task CreateSignalRPipeline_TimeoutExceptionNotRetried()
    {
        var pipeline = ResiliencePipelineFactory.CreateSignalRPipeline(Log.Logger, TimeSpan.FromSeconds(1));
        var callCount = 0;

        var act = () => pipeline.ExecuteAsync(async token =>
        {
            callCount++;
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
        }, CancellationToken.None).AsTask();

        await act.Should().ThrowAsync<TimeoutRejectedException>();
        callCount.Should().Be(1);
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
