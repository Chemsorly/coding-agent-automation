using AwesomeAssertions;
using Moq;
using Octokit;
using PipelineRateLimitExceededException = CodingAgentWebUI.Pipeline.Models.RateLimitExceededException;

namespace CodingAgentWebUI.Infrastructure.UnitTests.GitHub;

/// <summary>
/// Tests for ExecuteWithRateLimitHandlingAsync (Requirements 29.1–29.2).
/// Uses a TestableGitHubProvider subclass to expose the protected methods for direct testing.
/// </summary>
public class ExecuteWithRateLimitHandlingTests
{
    private readonly TestableGitHubProvider _provider;

    public ExecuteWithRateLimitHandlingTests()
    {
        var mockClient = new Mock<IGitHubClient>();
        _provider = new TestableGitHubProvider(mockClient.Object);
    }

    #region Generic overload (Task<T>)

    /// <summary>
    /// Requirement 29.1: Success pass-through — API call succeeds, result is returned unchanged.
    /// </summary>
    [Fact]
    public async Task GenericOverload_SuccessfulApiCall_ReturnsResultUnchanged()
    {
        var expected = "test-result";

        var result = await _provider.InvokeWithRateLimitHandlingAsync(() => Task.FromResult(expected));

        result.Should().Be(expected);
    }

    /// <summary>
    /// Requirement 29.1: RateLimitExceededException wrapping — Octokit.RateLimitExceededException
    /// is caught and wrapped in PipelineRateLimitExceededException.
    /// </summary>
    [Fact]
    public async Task GenericOverload_RateLimitExceededException_WrapsInPipelineException()
    {
        var response = CreateForbiddenResponse(retryAfterSeconds: null);
        var octokitException = new Octokit.RateLimitExceededException(response.Object);

        var act = () => _provider.InvokeWithRateLimitHandlingAsync<string>(
            () => throw octokitException);

        var ex = await act.Should().ThrowAsync<PipelineRateLimitExceededException>();
        ex.Which.InnerException.Should().BeSameAs(octokitException);
        ex.Which.ResetAt.Should().BeAfter(DateTimeOffset.MinValue);
    }

    /// <summary>
    /// Requirement 29.2: AbuseException wrapping — Octokit.AbuseException is caught
    /// and wrapped in PipelineRateLimitExceededException with RetryAfterSeconds.
    /// </summary>
    [Fact]
    public async Task GenericOverload_AbuseExceptionWithRetryAfter_WrapsInPipelineException()
    {
        var response = CreateForbiddenResponse(retryAfterSeconds: 120);
        var abuseException = new AbuseException(response.Object);

        var act = () => _provider.InvokeWithRateLimitHandlingAsync<string>(
            () => throw abuseException);

        var ex = await act.Should().ThrowAsync<PipelineRateLimitExceededException>();
        ex.Which.InnerException.Should().BeSameAs(abuseException);
        // ResetAt should be approximately now + 120 seconds
        ex.Which.ResetAt.Should().BeCloseTo(DateTimeOffset.UtcNow.AddSeconds(120), TimeSpan.FromSeconds(5));
    }

    /// <summary>
    /// Requirement 29.2: AbuseException without RetryAfterSeconds falls back to default wait.
    /// </summary>
    [Fact]
    public async Task GenericOverload_AbuseExceptionWithoutRetryAfter_UsesDefaultWait()
    {
        var response = CreateForbiddenResponse(retryAfterSeconds: null);
        var abuseException = new AbuseException(response.Object);

        var act = () => _provider.InvokeWithRateLimitHandlingAsync<string>(
            () => throw abuseException);

        var ex = await act.Should().ThrowAsync<PipelineRateLimitExceededException>();
        ex.Which.InnerException.Should().BeSameAs(abuseException);
        // Default wait is 60 seconds
        ex.Which.ResetAt.Should().BeCloseTo(DateTimeOffset.UtcNow.AddSeconds(60), TimeSpan.FromSeconds(5));
    }

    #endregion

    #region Non-generic overload (Task)

    /// <summary>
    /// Requirement 29.1: Success pass-through — void API call succeeds without throwing.
    /// </summary>
    [Fact]
    public async Task NonGenericOverload_SuccessfulApiCall_CompletesWithoutException()
    {
        var called = false;

        await _provider.InvokeWithRateLimitHandlingAsync(() =>
        {
            called = true;
            return Task.CompletedTask;
        });

        called.Should().BeTrue();
    }

    /// <summary>
    /// Requirement 29.1: RateLimitExceededException wrapping for void overload.
    /// </summary>
    [Fact]
    public async Task NonGenericOverload_RateLimitExceededException_WrapsInPipelineException()
    {
        var response = CreateForbiddenResponse(retryAfterSeconds: null);
        var octokitException = new Octokit.RateLimitExceededException(response.Object);

        var act = () => _provider.InvokeWithRateLimitHandlingAsync(
            () => throw octokitException);

        var ex = await act.Should().ThrowAsync<PipelineRateLimitExceededException>();
        ex.Which.InnerException.Should().BeSameAs(octokitException);
    }

    /// <summary>
    /// Requirement 29.2: AbuseException wrapping for void overload with RetryAfterSeconds.
    /// </summary>
    [Fact]
    public async Task NonGenericOverload_AbuseExceptionWithRetryAfter_WrapsInPipelineException()
    {
        var response = CreateForbiddenResponse(retryAfterSeconds: 90);
        var abuseException = new AbuseException(response.Object);

        var act = () => _provider.InvokeWithRateLimitHandlingAsync(
            () => throw abuseException);

        var ex = await act.Should().ThrowAsync<PipelineRateLimitExceededException>();
        ex.Which.InnerException.Should().BeSameAs(abuseException);
        ex.Which.ResetAt.Should().BeCloseTo(DateTimeOffset.UtcNow.AddSeconds(90), TimeSpan.FromSeconds(5));
    }

    /// <summary>
    /// Requirement 29.2: AbuseException without RetryAfterSeconds for void overload.
    /// </summary>
    [Fact]
    public async Task NonGenericOverload_AbuseExceptionWithoutRetryAfter_UsesDefaultWait()
    {
        var response = CreateForbiddenResponse(retryAfterSeconds: null);
        var abuseException = new AbuseException(response.Object);

        var act = () => _provider.InvokeWithRateLimitHandlingAsync(
            () => throw abuseException);

        var ex = await act.Should().ThrowAsync<PipelineRateLimitExceededException>();
        ex.Which.InnerException.Should().BeSameAs(abuseException);
        ex.Which.ResetAt.Should().BeCloseTo(DateTimeOffset.UtcNow.AddSeconds(60), TimeSpan.FromSeconds(5));
    }

    #endregion

    #region Helpers

    /// <summary>
    /// Creates a mock IResponse simulating a 403 Forbidden response with optional Retry-After header.
    /// </summary>
    private static Mock<IResponse> CreateForbiddenResponse(int? retryAfterSeconds)
    {
        var headers = new Dictionary<string, string>();
        if (retryAfterSeconds.HasValue)
        {
            headers["Retry-After"] = retryAfterSeconds.Value.ToString();
        }

        var resetTime = DateTimeOffset.UtcNow.AddMinutes(5);
        var rateLimit = new RateLimit(5000, 0, resetTime.ToUnixTimeSeconds());
        var apiInfo = new ApiInfo(
            new Dictionary<string, Uri>(), new List<string>(), new List<string>(),
            string.Empty, rateLimit);

        var response = new Mock<IResponse>();
        response.Setup(r => r.StatusCode).Returns(System.Net.HttpStatusCode.Forbidden);
        response.Setup(r => r.Headers).Returns(headers);
        response.Setup(r => r.Body).Returns("");
        response.Setup(r => r.ContentType).Returns("application/json");
        response.Setup(r => r.ApiInfo).Returns(apiInfo);

        return response;
    }

    #endregion

    #region Test infrastructure

    /// <summary>
    /// Test subclass that exposes the protected ExecuteWithRateLimitHandlingAsync methods
    /// for direct unit testing.
    /// </summary>
    private sealed class TestableGitHubProvider : CodingAgentWebUI.Infrastructure.GitHub.GitHubProviderBase
    {
        public TestableGitHubProvider(IGitHubClient client)
            : base(client, "test-owner", "test-repo")
        {
        }

        public Task<T> InvokeWithRateLimitHandlingAsync<T>(Func<Task<T>> apiCall)
            => ExecuteWithRateLimitHandlingAsync(apiCall);

        public Task InvokeWithRateLimitHandlingAsync(Func<Task> apiCall)
            => ExecuteWithRateLimitHandlingAsync(apiCall);
    }

    #endregion
}
