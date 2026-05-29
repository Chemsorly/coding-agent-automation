using System.Net;
using AwesomeAssertions;
using FsCheck;
using FsCheck.Fluent;
using FsCheck.Xunit;
using NGitLab;
using NGitLab.Mock;
using NGitLab.Mock.Config;
using CodingAgentWebUI.Infrastructure.GitLab;
using PipelineRateLimitExceededException = CodingAgentWebUI.Pipeline.Models.RateLimitExceededException;

namespace CodingAgentWebUI.Infrastructure.UnitTests.GitLab;

/// <summary>
/// Property-based tests for GitLab resilience pipeline behavior.
/// Tests retry logic, rate limit handling, and non-retryable error propagation.
/// Feature: 029-gitlab-providers, Properties 4 and 5.
/// </summary>
public class GitLabResiliencePipelineTests
{
    /// <summary>
    /// Concrete test subclass of <see cref="GitLabProviderBase"/> that exposes
    /// <c>ExecuteWithResilienceAsync</c> for direct testing of the resilience pipeline.
    /// </summary>
    private sealed class TestableGitLabProvider : GitLabProviderBase
    {
        public TestableGitLabProvider(IGitLabClient client, int projectId)
            : base(client, projectId)
        {
        }

        public Task<T> InvokeWithResilienceAsync<T>(
            Func<IGitLabClient, Task<T>> operation, string operationName, CancellationToken ct)
            => ExecuteWithResilienceAsync(operation, operationName, ct);

        public Task InvokeVoidWithResilienceAsync(
            Func<IGitLabClient, Task> operation, string operationName, CancellationToken ct)
            => ExecuteWithResilienceAsync(operation, operationName, ct);
    }

    /// <summary>
    /// Creates a test provider backed by NGitLab.Mock.
    /// </summary>
    private static TestableGitLabProvider CreateTestProvider()
    {
        var server = new GitLabConfig()
            .WithUser("TestUser", isDefault: true)
            .WithProject("TestProject", @namespace: "TestUser", addDefaultUserAsMaintainer: true)
            .BuildServer();

        var client = server.CreateClient();
        // Use project ID 1 (first project created in mock server)
        return new TestableGitLabProvider(client, 1);
    }

    #region Property 4: Rate limit triggers retry with exponential backoff

    /// <summary>
    /// Property 4: Rate limit triggers retry with exponential backoff.
    /// When a 429 response is returned on every attempt, the pipeline retries up to
    /// MaxRetryAttempts (3) times. After exhaustion, a <see cref="PipelineRateLimitExceededException"/>
    /// is thrown with a future ResetAt timestamp.
    /// **Validates: Requirements 3.2, 3.3, 3.4**
    /// </summary>
    [Property]
    public void RateLimit429_ExhaustsRetries_ThrowsRateLimitExceededException(PositiveInt _seed)
    {
        // Arrange: always throw 429 to exhaust all retries
        var provider = CreateTestProvider();
        var callCount = 0;

        // Act
        var act = () => provider.InvokeWithResilienceAsync<string>(async _ =>
        {
            Interlocked.Increment(ref callCount);
            throw new GitLabException("Rate limited") { StatusCode = (HttpStatusCode)429 };
        }, "RateLimitTest", CancellationToken.None);

        // Assert: should throw RateLimitExceededException after retries exhausted
        var exception = act.Should().ThrowAsync<PipelineRateLimitExceededException>()
            .GetAwaiter().GetResult();

        // The pipeline retries 3 times + 1 initial = 4 total attempts
        callCount.Should().Be(4);

        // ResetAt should be in the future (approximately 60 seconds from now)
        exception.Which.ResetAt.Should().BeAfter(DateTimeOffset.UtcNow);
    }

    /// <summary>
    /// Property 4 (partial): Rate limit 429 is retried — if it succeeds on a subsequent attempt,
    /// the result is returned successfully. Tests that the retry mechanism works for 429.
    /// **Validates: Requirements 3.2, 3.3**
    /// </summary>
    [Property]
    public void RateLimit429_SucceedsOnRetry_ReturnsResult(PositiveInt successAttempt)
    {
        // Arrange: fail with 429 a number of times within retry budget, then succeed
        var provider = CreateTestProvider();
        var callCount = 0;
        // Fail 1, 2, or 3 times before success (all within the 3-retry budget)
        var failCount = (successAttempt.Get % 3) + 1;

        // Act
        var result = provider.InvokeWithResilienceAsync(async _ =>
        {
            var attempt = Interlocked.Increment(ref callCount);
            if (attempt <= failCount)
                throw new GitLabException("Rate limited") { StatusCode = (HttpStatusCode)429 };
            return "success";
        }, "RateLimitRetryTest", CancellationToken.None).GetAwaiter().GetResult();

        // Assert: operation succeeded after retries
        result.Should().Be("success");
        callCount.Should().Be(failCount + 1);
    }

    #endregion

    #region Property 5: Non-retryable errors propagate immediately

    /// <summary>
    /// Property 5: Non-retryable errors propagate immediately.
    /// HTTP 401, 403, 404, 409, 422 responses are NOT retried — the exception propagates
    /// on the first attempt without any retry. For any randomly selected non-retryable
    /// status code, exactly one call is made.
    /// **Validates: Requirements 3.6, 25.2**
    /// </summary>
    [Property(Arbitrary = [typeof(NonRetryableStatusCodeArbitrary)])]
    public void NonRetryableStatusCode_PropagatesImmediately_NoRetry(HttpStatusCode statusCode)
    {
        // Arrange
        var provider = CreateTestProvider();
        var callCount = 0;

        // Act
        var act = () => provider.InvokeWithResilienceAsync<string>(async _ =>
        {
            Interlocked.Increment(ref callCount);
            throw new GitLabException($"Error {(int)statusCode}") { StatusCode = statusCode };
        }, "NonRetryableTest", CancellationToken.None);

        // Assert: should throw GitLabException immediately without retrying
        var exception = act.Should().ThrowAsync<GitLabException>().GetAwaiter().GetResult();
        exception.Which.StatusCode.Should().Be(statusCode);
        callCount.Should().Be(1, because: $"HTTP {(int)statusCode} should not be retried");
    }

    /// <summary>
    /// Property 5 (supplementary): Verifies each specific non-retryable code individually
    /// to ensure the resilience pipeline does not retry any of them.
    /// **Validates: Requirements 3.6, 25.2**
    /// </summary>
    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]       // 401
    [InlineData(HttpStatusCode.Forbidden)]          // 403
    [InlineData(HttpStatusCode.NotFound)]           // 404
    [InlineData(HttpStatusCode.Conflict)]           // 409
    [InlineData((HttpStatusCode)422)]               // UnprocessableEntity
    public async Task NonRetryableStatusCode_SingleAttempt_PropagatesException(HttpStatusCode statusCode)
    {
        // Arrange
        var provider = CreateTestProvider();
        var callCount = 0;

        // Act
        var act = () => provider.InvokeWithResilienceAsync<string>(async _ =>
        {
            Interlocked.Increment(ref callCount);
            throw new GitLabException($"Error {(int)statusCode}") { StatusCode = statusCode };
        }, "NonRetryableTest", CancellationToken.None);

        // Assert
        var exception = await act.Should().ThrowAsync<GitLabException>();
        exception.Which.StatusCode.Should().Be(statusCode);
        callCount.Should().Be(1, because: $"HTTP {(int)statusCode} should not be retried");
    }

    #endregion
}

#region Arbitraries

/// <summary>
/// Generates non-retryable HTTP status codes from the set [401, 403, 404, 409, 422].
/// These are status codes that should NOT trigger retry behavior in the resilience pipeline.
/// </summary>
public static class NonRetryableStatusCodeArbitrary
{
    public static Arbitrary<HttpStatusCode> HttpStatusCode()
    {
        var gen = Gen.Elements(
            System.Net.HttpStatusCode.Unauthorized,       // 401
            System.Net.HttpStatusCode.Forbidden,          // 403
            System.Net.HttpStatusCode.NotFound,           // 404
            System.Net.HttpStatusCode.Conflict,           // 409
            (System.Net.HttpStatusCode)422                // UnprocessableEntity
        );
        return gen.ToArbitrary();
    }
}

#endregion
