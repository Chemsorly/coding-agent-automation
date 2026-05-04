using AwesomeAssertions;
using Moq;
using Octokit;
using PipelineRateLimitExceededException = CodingAgentWebUI.Pipeline.Models.RateLimitExceededException;

namespace CodingAgentWebUI.Infrastructure.UnitTests.GitHub;

/// <summary>
/// Tests for ExecuteWithResilienceAsync — verifies retry on transient errors,
/// non-retryable exception passthrough, and rate limit backoff.
/// </summary>
public class GitHubProviderResilienceTests
{
    private readonly Mock<IGitHubClient> _mockClient;
    private readonly Mock<IRepositoriesClient> _mockRepos;
    private readonly TestableResilienceProvider _provider;

    public GitHubProviderResilienceTests()
    {
        _mockClient = new Mock<IGitHubClient>();
        _mockRepos = new Mock<IRepositoriesClient>();
        _mockClient.Setup(c => c.Repository).Returns(_mockRepos.Object);
        _provider = new TestableResilienceProvider(_mockClient.Object);
    }

    [Fact]
    public async Task ExecuteWithResilienceAsync_TransientError_RetriesAndSucceeds()
    {
        var callCount = 0;
        var result = await _provider.InvokeWithResilienceAsync(async _ =>
        {
            callCount++;
            if (callCount == 1)
                throw new HttpRequestException("DNS resolution failed");
            return "success";
        }, "TestOp", CancellationToken.None);

        result.Should().Be("success");
        callCount.Should().Be(2);
    }

    [Fact]
    public async Task ExecuteWithResilienceAsync_NotFoundException_FailsImmediately()
    {
        var callCount = 0;
        var act = () => _provider.InvokeWithResilienceAsync<string>(async _ =>
        {
            callCount++;
            throw new NotFoundException("Not Found", System.Net.HttpStatusCode.NotFound);
        }, "TestOp", CancellationToken.None);

        await act.Should().ThrowAsync<NotFoundException>();
        callCount.Should().Be(1); // No retry
    }

    [Fact]
    public async Task ExecuteWithResilienceAsync_AuthorizationException_FailsImmediately()
    {
        var callCount = 0;
        var act = () => _provider.InvokeWithResilienceAsync<string>(async _ =>
        {
            callCount++;
            throw new AuthorizationException(CreateMockResponse(System.Net.HttpStatusCode.Unauthorized));
        }, "TestOp", CancellationToken.None);

        await act.Should().ThrowAsync<AuthorizationException>();
        callCount.Should().Be(1); // No retry
    }

    [Fact]
    public async Task ExecuteWithResilienceAsync_5xxApiException_Retries()
    {
        var callCount = 0;
        var result = await _provider.InvokeWithResilienceAsync(async _ =>
        {
            callCount++;
            if (callCount == 1)
                throw new ApiException("Bad Gateway", System.Net.HttpStatusCode.BadGateway);
            return "success";
        }, "TestOp", CancellationToken.None);

        result.Should().Be("success");
        callCount.Should().Be(2);
    }

    [Fact]
    public async Task ExecuteWithResilienceAsync_RateLimitExceeded_RetriesWithBackoff()
    {
        var callCount = 0;
        var result = await _provider.InvokeWithResilienceAsync(async _ =>
        {
            callCount++;
            if (callCount == 1)
            {
                var response = CreateRateLimitResponse();
                throw new Octokit.RateLimitExceededException(response);
            }
            return "success";
        }, "TestOp", CancellationToken.None);

        result.Should().Be("success");
        callCount.Should().Be(2);
    }

    [Fact]
    public async Task ExecuteWithResilienceAsync_RateLimitExhausted_ThrowsPipelineRateLimitException()
    {
        var act = () => _provider.InvokeWithResilienceAsync<string>(async _ =>
        {
            var response = CreateRateLimitResponse();
            throw new Octokit.RateLimitExceededException(response);
        }, "TestOp", CancellationToken.None);

        await act.Should().ThrowAsync<PipelineRateLimitExceededException>();
    }

    [Fact]
    public async Task ExecuteWithResilienceAsync_MaxRetriesExhausted_ThrowsOriginalException()
    {
        var callCount = 0;
        var act = () => _provider.InvokeWithResilienceAsync<string>(async _ =>
        {
            callCount++;
            throw new HttpRequestException("persistent failure");
        }, "TestOp", CancellationToken.None);

        await act.Should().ThrowAsync<HttpRequestException>()
            .WithMessage("persistent failure");
        callCount.Should().Be(4); // 1 initial + 3 retries
    }

    [Fact]
    public async Task ExecuteWithResilienceAsync_VoidOverload_RetriesOnTransient()
    {
        var callCount = 0;
        await _provider.InvokeVoidWithResilienceAsync(async _ =>
        {
            callCount++;
            if (callCount == 1)
                throw new HttpRequestException("transient");
        }, "TestOp", CancellationToken.None);

        callCount.Should().Be(2);
    }

    private static IResponse CreateMockResponse(System.Net.HttpStatusCode statusCode)
    {
        var mock = new Mock<IResponse>();
        mock.Setup(r => r.StatusCode).Returns(statusCode);
        mock.Setup(r => r.Headers).Returns(new Dictionary<string, string>());
        mock.Setup(r => r.Body).Returns("");
        mock.Setup(r => r.ContentType).Returns("application/json");
        return mock.Object;
    }

    private static IResponse CreateRateLimitResponse()
    {
        var resetTime = DateTimeOffset.UtcNow.AddSeconds(1);
        var rateLimit = new RateLimit(5000, 0, resetTime.ToUnixTimeSeconds());
        var apiInfo = new ApiInfo(
            new Dictionary<string, Uri>(), new List<string>(), new List<string>(),
            string.Empty, rateLimit);

        var mock = new Mock<IResponse>();
        mock.Setup(r => r.StatusCode).Returns(System.Net.HttpStatusCode.Forbidden);
        mock.Setup(r => r.Headers).Returns(new Dictionary<string, string>());
        mock.Setup(r => r.Body).Returns("");
        mock.Setup(r => r.ContentType).Returns("application/json");
        mock.Setup(r => r.ApiInfo).Returns(apiInfo);
        return mock.Object;
    }

    /// <summary>
    /// Test subclass that exposes the protected ExecuteWithResilienceAsync methods.
    /// </summary>
    private sealed class TestableResilienceProvider : CodingAgentWebUI.Infrastructure.GitHub.GitHubProviderBase
    {
        public TestableResilienceProvider(IGitHubClient client)
            : base(client, "test-owner", "test-repo")
        {
        }

        public Task<T> InvokeWithResilienceAsync<T>(
            Func<IGitHubClient, Task<T>> operation, string operationName, CancellationToken ct)
            => ExecuteWithResilienceAsync(operation, operationName, ct);

        public Task InvokeVoidWithResilienceAsync(
            Func<IGitHubClient, Task> operation, string operationName, CancellationToken ct)
            => ExecuteWithResilienceAsync(operation, operationName, ct);
    }
}
