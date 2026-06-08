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

    private static Octokit.IResponse CreateMockResponse(System.Net.HttpStatusCode statusCode)
    {
        var mock = new Moq.Mock<Octokit.IResponse>();
        mock.Setup(r => r.StatusCode).Returns(statusCode);
        return mock.Object;
    }
}
